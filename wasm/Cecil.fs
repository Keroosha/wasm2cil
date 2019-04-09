
module wasm.cecil

    open Mono.Cecil
    open Mono.Cecil.Cil

    open wasm.def_basic
    open wasm.def
    open wasm.def_instr
    open wasm.module_index
    open wasm.instr_stack
    open wasm.import

    let mem_page_size = 64 * 1024

    type BasicTypes = {
        typ_void : TypeReference
        typ_i32 : TypeReference
        typ_i64 : TypeReference
        typ_f32 : TypeReference
        typ_f64 : TypeReference
        typ_intptr : TypeReference
        }

    let cecil_valtype bt vt =
        match vt with
        | I32 -> bt.typ_i32
        | I64 -> bt.typ_i64
        | F32 -> bt.typ_f32
        | F64 -> bt.typ_f64

    type ParamRef = {
        typ : ValType;
        def_param : ParameterDefinition;
        }

    type LocalRef = {
        typ : ValType;
        def_var : VariableDefinition;
        }

    type ParamOrVar =
        | ParamRef of ParamRef
        | LocalRef of LocalRef

    type GlobalRefImported = {
        glob : ImportedGlobal
        field : FieldReference
        }

    type GlobalRefInternal = {
        glob : InternalGlobal
        field : FieldDefinition
        }

    type GlobalStuff = 
        | GlobalRefImported of GlobalRefImported
        | GlobalRefInternal of GlobalRefInternal

    type MethodRefImported = {
        func : ImportedFunc
        method : MethodReference
        }

    type MethodRefInternal = {
        func : InternalFunc
        method : MethodDefinition
        }

    type MethodStuff = 
        | MethodRefImported of MethodRefImported
        | MethodRefInternal of MethodRefInternal

    type GenContext = {
        types: FuncType[]
        md : ModuleDefinition
        mem : FieldReference
        mem_size : FieldReference
        tbl_lookup : MethodDefinition
        a_globals : GlobalStuff[]
        a_methods : MethodStuff[]
        bt : BasicTypes
        }

    type DataStuff = {
        item : DataItem
        name : string
        resource : EmbeddedResource
        }

    let get_function_name fidx f =
        match f with
        | ImportedFunc i -> sprintf "%s.%s" i.m i.name
        | InternalFunc i -> 
            match i.name with
            | Some s -> s
            | None -> sprintf "func_%d" fidx

    type CodeBlock =
        | CB_Block of Mono.Cecil.Cil.Instruction
        | CB_Loop of Mono.Cecil.Cil.Instruction
        | CB_If of Mono.Cecil.Cil.Instruction
        | CB_Else of Mono.Cecil.Cil.Instruction

    let make_tmp (method : MethodDefinition) (t : TypeReference) =
        let v = new VariableDefinition(t)
        method.Body.Variables.Add(v)
        v

    let cecil_expr (il: ILProcessor) ctx (f_make_tmp : TypeReference -> VariableDefinition) (a_locals : ParamOrVar[]) e =
        let blocks = System.Collections.Generic.Stack<CodeBlock>()
        let stack = System.Collections.Generic.Stack<ValType>()
        let lab_end = il.Create(OpCodes.Nop)

        let get_label (a :CodeBlock[]) i =
            let (LabelIdx i) = i
            let blk = a.[int i]
            let lab =
                match blk with
                | CB_Block s -> s
                | CB_Loop s -> s
                | CB_If s -> s
                | CB_Else s -> s
            lab

        let prep_addr (m : MemArg) =
            // the address operand should be on the stack
            il.Append(il.Create(OpCodes.Ldsfld, ctx.mem))
            il.Append(il.Create(OpCodes.Add))
            if m.offset <> 0u then
                il.Append(il.Create(OpCodes.Ldc_I4, int m.offset))
                il.Append(il.Create(OpCodes.Add))

        let load m op =
            prep_addr m
            il.Append(il.Create(op))

        let store m op (tmp : VariableDefinition) =
            il.Append(il.Create(OpCodes.Stloc, tmp)) // pop v into tmp
            prep_addr m
            il.Append(il.Create(OpCodes.Ldloc, tmp)) // put v back
            il.Append(il.Create(op))

        let todo q =
            printfn "TODO: %A" q
        for op in e do
            let stack_info = get_instruction_stack_info op

(*
            printfn "op: %A" op
            printfn "stack_info: %A" stack_info
            printfn "stack depth: %d" stack.Count
*)
            let result_type =
                let handle_stack_for_call ftype =
                    if ftype.parms.Length > 0 then
                        for i = (ftype.parms.Length - 1) downto 0 do
                            let should = ftype.parms.[i]
                            let actual = stack.Pop();
                            if actual <> should then failwith "type mismatch"
                    if ftype.result.Length = 0 then
                        None
                    else if ftype.result.Length = 1 then
                        ftype.result.[0] |> Some
                    else
                        failwith "not implemented"

                match stack_info with
                | NoArgs rt -> rt
                | OneArg { rtype = rt; arg = t1; } ->
                    let arg1 = stack.Pop()
                    if arg1 <> t1 then failwith "wrong stack type"
                    rt
                | TwoArgs { rtype = rt; arg1 = t1; arg2 = t2; } ->
                    let arg2 = stack.Pop()
                    let arg1 = stack.Pop()
                    if arg1 <> t1 then failwith "wrong stack type"
                    if arg2 <> t2 then failwith "wrong stack type"
                    rt
                | SpecialCaseDrop ->
                    stack.Pop() |> ignore
                    None
                | SpecialCaseSelect ->
                    let arg3 = stack.Pop()
                    let arg2 = stack.Pop()
                    let arg1 = stack.Pop()
                    if arg3 <> I32 then failwith "Select type wrong"
                    if arg1 <> arg2 then failwith "wrong stack type"
                    Some arg1
                | SpecialCaseCall (FuncIdx fidx) ->
                    let fn = ctx.a_methods.[int fidx]
                    let ftype = 
                        match fn with
                        | MethodRefImported mf -> mf.func.typ
                        | MethodRefInternal mf -> mf.func.typ
                    handle_stack_for_call ftype
                | SpecialCaseCallIndirect calli ->
                    let (TypeIdx tidx) = calli.typeidx
                    let ftype = ctx.types.[int tidx]
                    handle_stack_for_call ftype
                | SpecialCaseLocalSet (LocalIdx i) ->
                    let loc = a_locals.[int i]
                    let typ =
                        match loc with
                        | ParamRef { typ = t } -> t
                        | LocalRef { typ = t } -> t
                    let arg = stack.Pop()
                    if arg <> typ then failwith "type mismatch"
                    None
                | SpecialCaseLocalGet (LocalIdx i) ->
                    let loc = a_locals.[int i]
                    let typ =
                        match loc with
                        | ParamRef { typ = t } -> t
                        | LocalRef { typ = t } -> t
                    Some typ
                | SpecialCaseGlobalSet (GlobalIdx i) ->
                    let g = ctx.a_globals.[int i]
                    let typ = 
                        match g with
                        | GlobalRefImported mf -> mf.glob.typ.typ
                        | GlobalRefInternal mf -> mf.glob.item.globaltype.typ
                    let arg = stack.Pop()
                    if arg <> typ then failwith "type mismatch"
                    None
                | SpecialCaseGlobalGet (GlobalIdx i) ->
                    let g = ctx.a_globals.[int i]
                    let typ = 
                        match g with
                        | GlobalRefImported mf -> mf.glob.typ.typ
                        | GlobalRefInternal mf -> mf.glob.item.globaltype.typ
                    Some typ
                | SpecialCaseLocalTee (LocalIdx i) ->
                    let loc = a_locals.[int i]
                    let typ =
                        match loc with
                        | ParamRef { typ = t } -> t
                        | LocalRef { typ = t } -> t
                    let arg = stack.Pop()
                    if arg <> typ then failwith "type mismatch"
                    stack.Push(arg);
                    None

            match op with
            | Nop -> il.Append(il.Create(OpCodes.Nop))
            | Block t -> 
                let lab = il.Create(OpCodes.Nop)
                let blk = CB_Block lab
                blocks.Push(blk)
            | Loop t -> 
                let lab = il.Create(OpCodes.Nop)
                let blk = CB_Loop lab
                blocks.Push(blk)
                il.Append(lab)
            | If t -> 
                let lab = il.Create(OpCodes.Nop)
                let blk = CB_If lab
                blocks.Push(blk)
            | Else -> 
                // first, end the if block
                match blocks.Pop() with
                | CB_If lab -> il.Append(lab)
                | _ -> failwith "bad nest"

                let lab = il.Create(OpCodes.Nop)
                let blk = CB_Else lab
                blocks.Push(blk)
            | End -> 
                if blocks.Count = 0 then
                    il.Append(lab_end) // TODO only gen this if it is needed
                else
                    let blk = blocks.Pop()
                    match blk with
                    | CB_Block lab -> il.Append(lab)
                    | CB_Loop _ -> () // loop label was at the top
                    | CB_If lab -> il.Append(lab)
                    | CB_Else lab -> il.Append(lab)
            | Return ->
                il.Append(il.Create(OpCodes.Br, lab_end))
            | Br i ->
                let a = blocks.ToArray()
                let lab = get_label a i
                il.Append(il.Create(OpCodes.Br, lab))
            | BrIf i ->
                let a = blocks.ToArray()
                let lab = get_label a i
                il.Append(il.Create(OpCodes.Brtrue, lab))
            | BrTable m ->
                let a = blocks.ToArray()
                let q = Array.map (fun i -> get_label a i) m.v
                il.Append(il.Create(OpCodes.Switch, q))
                let lab = get_label a m.other
                il.Append(il.Create(OpCodes.Br, lab))

            | Call (FuncIdx fidx) ->
                let fn = ctx.a_methods.[int fidx]
                match fn with
                | MethodRefImported mf ->
                    il.Append(il.Create(OpCodes.Call, mf.method))
                | MethodRefInternal mf ->
                    il.Append(il.Create(OpCodes.Call, mf.method))

            | CallIndirect _ ->
                let cs =
                    match stack_info with
                    | SpecialCaseCallIndirect calli ->
                        let cs = 
                            match result_type with
                            | Some t -> CallSite(cecil_valtype ctx.bt t)
                            | None -> CallSite(ctx.bt.typ_void)
                        let (TypeIdx tidx) = calli.typeidx
                        let ftype = ctx.types.[int tidx]
                        for a in ftype.parms do
                            cs.Parameters.Add(ParameterDefinition(cecil_valtype ctx.bt a))
                        cs
                    | _ -> failwith "should not happen"
                il.Append(il.Create(OpCodes.Call, ctx.tbl_lookup))
                il.Append(il.Create(OpCodes.Calli, cs))

            | GlobalGet (GlobalIdx idx) ->
                let g = ctx.a_globals.[int idx]
                match g with
                | GlobalRefImported mf ->
                    il.Append(il.Create(OpCodes.Ldsfld, mf.field))
                | GlobalRefInternal mf ->
                    il.Append(il.Create(OpCodes.Ldsfld, mf.field))

            | GlobalSet (GlobalIdx idx) ->
                let g = ctx.a_globals.[int idx]
                match g with
                | GlobalRefImported mf ->
                    il.Append(il.Create(OpCodes.Stsfld, mf.field))
                | GlobalRefInternal mf ->
                    il.Append(il.Create(OpCodes.Stsfld, mf.field))

            | LocalTee (LocalIdx i) -> 
                il.Append(il.Create(OpCodes.Dup))
                let loc = a_locals.[int i]
                match loc with
                | ParamRef { def_param = n } -> il.Append(il.Create(OpCodes.Starg, n))
                | LocalRef { def_var = n } -> il.Append(il.Create(OpCodes.Stloc, n))

            | LocalSet (LocalIdx i) -> 
                let loc = a_locals.[int i]
                match loc with
                | ParamRef { def_param = n } -> il.Append(il.Create(OpCodes.Starg, n))
                | LocalRef { def_var = n } -> il.Append(il.Create(OpCodes.Stloc, n))

            | LocalGet (LocalIdx i) -> 
                let loc = a_locals.[int i]
                match loc with
                | ParamRef { def_param = n } -> il.Append(il.Create(OpCodes.Ldarg, n))
                | LocalRef { def_var = n } -> il.Append(il.Create(OpCodes.Ldloc, n))

            | I32Load m -> load m OpCodes.Ldind_I4
            | I64Load m -> load m OpCodes.Ldind_I8
            | F32Load m -> load m OpCodes.Ldind_R4
            | F64Load m -> load m OpCodes.Ldind_R8
            | I32Load8S m -> load m OpCodes.Ldind_I1
            | I32Load8U m -> load m OpCodes.Ldind_U1
            | I32Load16S m -> load m OpCodes.Ldind_I2
            | I32Load16U m -> load m OpCodes.Ldind_U2
            | I64Load8S m -> load m OpCodes.Ldind_I1
            | I64Load8U m -> load m OpCodes.Ldind_U1
            | I64Load16S m -> load m OpCodes.Ldind_I2
            | I64Load16U m -> load m OpCodes.Ldind_U2
            | I64Load32S m -> load m OpCodes.Ldind_I4
            | I64Load32U m -> load m OpCodes.Ldind_U4

            | I32Store m -> store m OpCodes.Stind_I4 (f_make_tmp ctx.bt.typ_i32)
            | I64Store m -> store m OpCodes.Stind_I8 (f_make_tmp ctx.bt.typ_i64)
            | F32Store m -> store m OpCodes.Stind_R4 (f_make_tmp ctx.bt.typ_f32)
            | F64Store m -> store m OpCodes.Stind_R8 (f_make_tmp ctx.bt.typ_f64)
            | I32Store8 m -> store m OpCodes.Stind_I1 (f_make_tmp ctx.bt.typ_i32)
            | I32Store16 m -> store m OpCodes.Stind_I2 (f_make_tmp ctx.bt.typ_i32)
            | I64Store8 m -> store m OpCodes.Stind_I1 (f_make_tmp ctx.bt.typ_i64)
            | I64Store16 m -> store m OpCodes.Stind_I2 (f_make_tmp ctx.bt.typ_i64)
            | I64Store32 m -> store m OpCodes.Stind_I4 (f_make_tmp ctx.bt.typ_i64)

            | I32Const i -> il.Append(il.Create(OpCodes.Ldc_I4, i))
            | I64Const i -> il.Append(il.Create(OpCodes.Ldc_I8, i))
            | F32Const i -> il.Append(il.Create(OpCodes.Ldc_R4, i))
            | F64Const i -> il.Append(il.Create(OpCodes.Ldc_R8, i))

            | I32Add | I64Add | F32Add | F64Add -> il.Append(il.Create(OpCodes.Add))
            | I32Mul | I64Mul | F32Mul | F64Mul -> il.Append(il.Create(OpCodes.Mul))
            | I32Sub | I64Sub | F32Sub | F64Sub -> il.Append(il.Create(OpCodes.Sub))
            | I32DivS | I64DivS | F32Div | F64Div -> il.Append(il.Create(OpCodes.Div))
            | I32DivU | I64DivU -> il.Append(il.Create(OpCodes.Div_Un))

            | F64Abs ->
                let ext = ctx.md.ImportReference(typeof<System.Math>.GetMethod("Abs", [| typeof<double> |] ))
                il.Append(il.Create(OpCodes.Call, ext))
            | F64Sqrt ->
                let ext = ctx.md.ImportReference(typeof<System.Math>.GetMethod("Sqrt", [| typeof<double> |] ))
                il.Append(il.Create(OpCodes.Call, ext))
            | F64Ceil ->
                let ext = ctx.md.ImportReference(typeof<System.Math>.GetMethod("Ceiling", [| typeof<double> |] ))
                il.Append(il.Create(OpCodes.Call, ext))
            | F64Floor ->
                let ext = ctx.md.ImportReference(typeof<System.Math>.GetMethod("Floor", [| typeof<double> |] ))
                il.Append(il.Create(OpCodes.Call, ext))
            | F64Trunc ->
                let ext = ctx.md.ImportReference(typeof<System.Math>.GetMethod("Truncate", [| typeof<double> |] ))
                il.Append(il.Create(OpCodes.Call, ext))
            | F64Nearest ->
                let ext = ctx.md.ImportReference(typeof<System.Math>.GetMethod("Round", [| typeof<double> |] ))
                il.Append(il.Create(OpCodes.Call, ext))
            | F64Min ->
                let ext = ctx.md.ImportReference(typeof<System.Math>.GetMethod("Min", [| typeof<double>; typeof<double> |] ))
                il.Append(il.Create(OpCodes.Call, ext))
            | F64Max ->
                let ext = ctx.md.ImportReference(typeof<System.Math>.GetMethod("Max", [| typeof<double>; typeof<double> |] ))
                il.Append(il.Create(OpCodes.Call, ext))

            | F32Abs ->
                let ext = ctx.md.ImportReference(typeof<System.Math>.GetMethod("Abs", [| typeof<float32> |] ))
                il.Append(il.Create(OpCodes.Call, ext))
            | F32Sqrt ->
                let ext = ctx.md.ImportReference(typeof<System.Math>.GetMethod("Sqrt", [| typeof<float32> |] ))
                il.Append(il.Create(OpCodes.Call, ext))
            | F32Ceil ->
                let ext = ctx.md.ImportReference(typeof<System.Math>.GetMethod("Ceiling", [| typeof<float32> |] ))
                il.Append(il.Create(OpCodes.Call, ext))
            | F32Floor ->
                let ext = ctx.md.ImportReference(typeof<System.Math>.GetMethod("Floor", [| typeof<float32> |] ))
                il.Append(il.Create(OpCodes.Call, ext))
            | F32Trunc ->
                let ext = ctx.md.ImportReference(typeof<System.Math>.GetMethod("Truncate", [| typeof<float32> |] ))
                il.Append(il.Create(OpCodes.Call, ext))
            | F32Nearest ->
                let ext = ctx.md.ImportReference(typeof<System.Math>.GetMethod("Round", [| typeof<float32> |] ))
                il.Append(il.Create(OpCodes.Call, ext))
            | F32Min ->
                let ext = ctx.md.ImportReference(typeof<System.Math>.GetMethod("Min", [| typeof<float32>; typeof<float32> |] ))
                il.Append(il.Create(OpCodes.Call, ext))
            | F32Max ->
                let ext = ctx.md.ImportReference(typeof<System.Math>.GetMethod("Max", [| typeof<float32>; typeof<float32> |] ))
                il.Append(il.Create(OpCodes.Call, ext))

            | I32Eqz ->
                il.Append(il.Create(OpCodes.Ldc_I4_0))
                il.Append(il.Create(OpCodes.Ceq))
                
            | I64Eqz ->
                il.Append(il.Create(OpCodes.Ldc_I8, 0L))
                il.Append(il.Create(OpCodes.Ceq))
                
            | I32LtS | I64LtS | F32Lt | F64Lt -> il.Append(il.Create(OpCodes.Clt))
            | I32LtU | I64LtU -> il.Append(il.Create(OpCodes.Clt_Un))

            | I32GtS | I64GtS | F32Gt | F64Gt -> il.Append(il.Create(OpCodes.Cgt))
            | I32GtU | I64GtU -> il.Append(il.Create(OpCodes.Cgt_Un))

            | F32Neg -> il.Append(il.Create(OpCodes.Neg))
            | F64Neg -> il.Append(il.Create(OpCodes.Neg))

            | I32Eq | I64Eq | F32Eq | F64Eq -> il.Append(il.Create(OpCodes.Ceq))

            | I32And | I64And -> il.Append(il.Create(OpCodes.And))
            | I32Or | I64Or -> il.Append(il.Create(OpCodes.Or))
            | I32Xor | I64Xor -> il.Append(il.Create(OpCodes.Xor))

            | I32Shl | I64Shl -> il.Append(il.Create(OpCodes.Shl))
            | I32ShrS | I64ShrS -> il.Append(il.Create(OpCodes.Shr))
            | I32ShrU | I64ShrU -> il.Append(il.Create(OpCodes.Shr_Un))
            | I32RemS | I64RemS -> il.Append(il.Create(OpCodes.Rem))
            | I32RemU | I64RemU -> il.Append(il.Create(OpCodes.Rem_Un))

            | F32ConvertI32S | F32ConvertI64S | F32DemoteF64 -> il.Append(il.Create(OpCodes.Conv_R4))
            | F64ConvertI32S | F64ConvertI64S | F64PromoteF32 -> il.Append(il.Create(OpCodes.Conv_R8))

            | F32ConvertI32U | F32ConvertI64U ->
                il.Append(il.Create(OpCodes.Conv_R_Un))
                il.Append(il.Create(OpCodes.Conv_R4))

            | F64ConvertI32U | F64ConvertI64U ->
                il.Append(il.Create(OpCodes.Conv_R_Un))
                il.Append(il.Create(OpCodes.Conv_R8))

            | I32WrapI64 -> il.Append(il.Create(OpCodes.Conv_I4))
            | I64ExtendI32S | I64ExtendI32U -> il.Append(il.Create(OpCodes.Conv_I8))

            | I32TruncF32S | I32TruncF64S -> il.Append(il.Create(OpCodes.Conv_Ovf_I4))
            | I64TruncF32S | I64TruncF64S -> il.Append(il.Create(OpCodes.Conv_Ovf_I8))
            | I32TruncF32U | I32TruncF64U -> il.Append(il.Create(OpCodes.Conv_Ovf_I4_Un))
            | I64TruncF32U | I64TruncF64U -> il.Append(il.Create(OpCodes.Conv_Ovf_I8_Un))

            | I32Ne | I64Ne | F32Ne | F64Ne -> 
                il.Append(il.Create(OpCodes.Ceq))
                il.Append(il.Create(OpCodes.Ldc_I4_0))
                il.Append(il.Create(OpCodes.Ceq))

            | I32LeS | I64LeS ->
                il.Append(il.Create(OpCodes.Cgt))
                il.Append(il.Create(OpCodes.Ldc_I4_0))
                il.Append(il.Create(OpCodes.Ceq))

            | I32GeS | I64GeS ->
                il.Append(il.Create(OpCodes.Clt))
                il.Append(il.Create(OpCodes.Ldc_I4_0))
                il.Append(il.Create(OpCodes.Ceq))

            | I32LeU | I64LeU ->
                il.Append(il.Create(OpCodes.Cgt_Un))
                il.Append(il.Create(OpCodes.Ldc_I4_0))
                il.Append(il.Create(OpCodes.Ceq))

            | I32GeU | I64GeU ->
                il.Append(il.Create(OpCodes.Clt_Un))
                il.Append(il.Create(OpCodes.Ldc_I4_0))
                il.Append(il.Create(OpCodes.Ceq))

            | F32Le | F64Le ->
                il.Append(il.Create(OpCodes.Cgt_Un))
                il.Append(il.Create(OpCodes.Ldc_I4_0))
                il.Append(il.Create(OpCodes.Ceq))

            | F32Ge | F64Ge ->
                il.Append(il.Create(OpCodes.Clt_Un))
                il.Append(il.Create(OpCodes.Ldc_I4_0))
                il.Append(il.Create(OpCodes.Ceq))

            | Drop -> il.Append(il.Create(OpCodes.Pop))

            | Unreachable -> todo op
            | Select -> 
                match result_type with
                | Some t ->
                    let var_c = f_make_tmp ctx.bt.typ_i32
                    il.Append(il.Create(OpCodes.Stloc, var_c))
                    let var_v2 = f_make_tmp (cecil_valtype ctx.bt t)
                    il.Append(il.Create(OpCodes.Stloc, var_v2))
                    let var_v1 = f_make_tmp (cecil_valtype ctx.bt t)
                    il.Append(il.Create(OpCodes.Stloc, var_v1))

                    let push_v1 = il.Create(OpCodes.Ldloc, var_v1);
                    let push_v2 = il.Create(OpCodes.Ldloc, var_v2);
                    let lab_done = il.Create(OpCodes.Nop);

                    il.Append(il.Create(OpCodes.Ldloc, var_c)) // put c back
                    il.Append(il.Create(OpCodes.Brtrue, push_v1))
                    il.Append(push_v2);
                    il.Append(il.Create(OpCodes.Br, lab_done));
                    il.Append(push_v1);
                    il.Append(lab_done);
                | None -> failwith "should not happen"
            | MemorySize _ -> il.Append(il.Create(OpCodes.Ldsfld, ctx.mem_size))
            | MemoryGrow _ -> todo op
            | I32Clz -> todo op
            | I32Ctz -> todo op
            | I32Popcnt -> todo op
            | I32Rotl -> todo op
            | I32Rotr -> todo op
            | I64Clz -> todo op
            | I64Ctz -> todo op
            | I64Popcnt -> todo op
            | I64Rotl -> todo op
            | I64Rotr -> todo op
            | F32Copysign -> todo op
            | F64Copysign -> todo op
            | I32ReinterpretF32 -> todo op
            | I64ReinterpretF64 -> todo op
            | F32ReinterpretI32 -> todo op
            | F64ReinterpretI64 -> todo op

            match result_type with
            | Some t -> stack.Push(t)
            | None -> ()

    let create_global (gi : InternalGlobal) idx bt =
        let name = 
            match gi.name with
            | Some s -> s
            | None -> sprintf "global_%d" idx

        let typ = cecil_valtype bt gi.item.globaltype.typ

        let access = if gi.exported then FieldAttributes.Public else FieldAttributes.Private

        let method = 
            new FieldDefinition(
                name,
                access ||| Mono.Cecil.FieldAttributes.Static, 
                typ
                )

        method

    let create_method (fi : InternalFunc) fidx bt =
        let name = 
            match fi.name with
            | Some s -> s
            | None -> sprintf "func_%d" fidx

        let return_type =
            match fi.typ.result.Length with
            | 0 -> bt.typ_void
            | 1 -> cecil_valtype bt (fi.typ.result.[0])
            | _ -> failwith "not implemented"

        let access = if fi.exported then MethodAttributes.Public else MethodAttributes.Private

        let method = 
            new MethodDefinition(
                name,
                access ||| Mono.Cecil.MethodAttributes.Static, 
                return_type
                )

        method

    let gen_function_code ctx (mi : MethodRefInternal) =
        let a_locals =
            let a = System.Collections.Generic.List<ParamOrVar>()
            let get_name () =
                sprintf "p%d" (a.Count)
            for x in mi.func.typ.parms do
                let typ = cecil_valtype ctx.bt x
                let name = get_name()
                let def = new ParameterDefinition(name, ParameterAttributes.None, typ)
                a.Add(ParamRef { def_param = def; typ = x; })
            for loc in mi.func.code.locals do
                // TODO assert count > 0 ?
                for x = 1 to (int loc.count) do
                    let typ = cecil_valtype ctx.bt loc.localtype
                    let def = new VariableDefinition(typ)
                    a.Add(LocalRef { def_var = def; typ = loc.localtype })
            Array.ofSeq a
            
        mi.method.Body.InitLocals <- true
        for pair in a_locals do
            match pair with
            | ParamRef { def_param = def } -> mi.method.Parameters.Add(def)
            | LocalRef { def_var = def } -> mi.method.Body.Variables.Add(def)

        let f_make_tmp = make_tmp mi.method

        let il = mi.method.Body.GetILProcessor()
        cecil_expr il ctx f_make_tmp a_locals mi.func.code.expr
        il.Append(il.Create(OpCodes.Ret))

    let create_methods ndx bt (md : ModuleDefinition) assy =
        let count_imports = count_function_imports ndx
        let prep_func i fi =
            match fi with
            | ImportedFunc s ->
                let method = import_function md s assy
                MethodRefImported { MethodRefImported.func = s; method = method }
            | InternalFunc q ->
                let method = create_method q (count_imports + i) bt
                MethodRefInternal { func = q; method = method; }

        let a_methods = Array.mapi prep_func ndx.FuncLookup
        a_methods

    let gen_code_for_methods ctx =
        for m in ctx.a_methods do
            match m with
            | MethodRefInternal mi -> gen_function_code ctx mi
            | MethodRefImported _ -> ()

    let create_globals ndx bt (md : ModuleDefinition) assy =
        let count_imports = count_global_imports ndx

        let prep i gi =
            match gi with
            | ImportedGlobal s ->
                let field = import_global md s assy
                GlobalRefImported { GlobalRefImported.glob = s; field = field; }
            | InternalGlobal q ->
                let field = create_global q (count_imports + i) bt
                GlobalRefInternal { glob = q; field = field; }

        let a_globals = Array.mapi prep ndx.GlobalLookup

        a_globals

    let gen_tbl_lookup ndx bt (tbl : FieldDefinition) =
        let method = 
            new MethodDefinition(
                "__tbl_lookup",
                MethodAttributes.Public ||| MethodAttributes.Static,
                bt.typ_intptr
                )
        let parm = new ParameterDefinition(bt.typ_i32)
        method.Parameters.Add(parm)

        // TODO this needs to do type check, range check

        let il = method.Body.GetILProcessor()

        il.Append(il.Create(OpCodes.Ldsfld, tbl))
        il.Append(il.Create(OpCodes.Ldarg, parm))
        il.Append(il.Create(OpCodes.Ldc_I4, 8))
        il.Append(il.Create(OpCodes.Mul))
        il.Append(il.Create(OpCodes.Add))
        il.Append(il.Create(OpCodes.Ldind_I))
        il.Append(il.Create(OpCodes.Ret))

        method

    let gen_data_setup ndx ctx (a_env : System.Reflection.Assembly) (a_datas : DataStuff[]) =
        let method = 
            new MethodDefinition(
                "__data_setup",
                MethodAttributes.Public ||| MethodAttributes.Static,
                ctx.bt.typ_void
                )
        let il = method.Body.GetILProcessor()

        // need to grab a reference to this assembly 
        // (the one that contains our resources)
        // and store it in a local so we can use it
        // each time through the loop.

        let ref_typ_assembly = ctx.md.ImportReference(typeof<System.Reflection.Assembly>)
        let loc_assembly = new VariableDefinition(ref_typ_assembly)
        method.Body.Variables.Add(loc_assembly)
        let ref_gea = ctx.md.ImportReference(typeof<System.Reflection.Assembly>.GetMethod("GetExecutingAssembly"))
        il.Append(il.Create(OpCodes.Call, ref_gea))
        il.Append(il.Create(OpCodes.Stloc, loc_assembly))

        // also need to import Marshal.Copy

        let ref_mcopy = ctx.md.ImportReference(typeof<System.Runtime.InteropServices.Marshal>.GetMethod("Copy", [| typeof<byte[]>; typeof<int32>; typeof<nativeint>; typeof<int32> |]))

        // grab GetResource from env

        let ref_getresource =
            let typ = a_env.GetType("env")
            let method = typ.GetMethod("GetResource")
            let mref = ctx.md.ImportReference(method)
            mref

        for d in a_datas do
            // the 4 args to Marshal.Copy...

            // the byte array containing the resource
            il.Append(il.Create(OpCodes.Ldloc, loc_assembly))
            il.Append(il.Create(OpCodes.Ldstr, d.name))
            il.Append(il.Create(OpCodes.Call, ref_getresource))

            // 0, the offset
            il.Append(il.Create(OpCodes.Ldc_I4, 0))

            // the destination pointer
            il.Append(il.Create(OpCodes.Ldsfld, ctx.mem))
            let f_make_tmp = make_tmp method
            cecil_expr il ctx f_make_tmp Array.empty d.item.offset
            il.Append(il.Create(OpCodes.Add))

            // and the length
            il.Append(il.Create(OpCodes.Ldc_I4, d.item.init.Length))

            // now copy
            il.Append(il.Create(OpCodes.Call, ref_mcopy))

        il.Append(il.Create(OpCodes.Ret))

        method

    let gen_tbl_setup ndx ctx (tbl : FieldDefinition) lim (elems : ElementItem[]) =
        let method = 
            new MethodDefinition(
                "__tbl_setup",
                MethodAttributes.Public ||| MethodAttributes.Static,
                ctx.bt.typ_void
                )
        let il = method.Body.GetILProcessor()

        let count_tbl_entries = 
            match lim with
            | Min m -> m
            | MinMax (min,max) -> max
        let size_in_bytes = (int count_tbl_entries) * 8 // TODO nativeint size 4 vs 8 

        il.Append(il.Create(OpCodes.Ldc_I4, size_in_bytes))
        // TODO where is this freed?
        let ext = ctx.mem.Module.ImportReference(typeof<System.Runtime.InteropServices.Marshal>.GetMethod("AllocHGlobal", [| typeof<int32> |] ))
        il.Append(il.Create(OpCodes.Call, ext))
        il.Append(il.Create(OpCodes.Stsfld, tbl))

        // memset 0
        il.Append(il.Create(OpCodes.Ldsfld, tbl))
        il.Append(il.Create(OpCodes.Ldc_I4, 0))
        il.Append(il.Create(OpCodes.Ldc_I4, size_in_bytes))
        il.Append(il.Create(OpCodes.Initblk))

        let f_make_tmp = make_tmp method

        let tmp = VariableDefinition(ctx.bt.typ_i32)
        method.Body.Variables.Add(tmp)

        for elem in elems do
            cecil_expr il ctx f_make_tmp Array.empty elem.offset
            il.Append(il.Create(OpCodes.Stloc, tmp))
            for i = 0 to (elem.init.Length - 1) do

                // prep the addr
                il.Append(il.Create(OpCodes.Ldsfld, tbl))
                il.Append(il.Create(OpCodes.Ldloc, tmp))
                il.Append(il.Create(OpCodes.Ldc_I4, i))
                il.Append(il.Create(OpCodes.Add))
                il.Append(il.Create(OpCodes.Ldc_I4, 8))
                il.Append(il.Create(OpCodes.Mul))
                il.Append(il.Create(OpCodes.Add))

                // now the func ptr
                let (FuncIdx fidx) = elem.init.[i]
                let m = ctx.a_methods.[int fidx]
                let m = 
                    match m with
                    | MethodRefImported m -> m.method
                    | MethodRefInternal m -> m.method :> MethodReference
                il.Append(il.Create(OpCodes.Ldftn, m))

                // and store it
                il.Append(il.Create(OpCodes.Stind_I))

        il.Append(il.Create(OpCodes.Ret))

        method

    let gen_cctor ctx (tbl_setup : MethodDefinition option) (data_setup : MethodDefinition option) =
        let method = 
            new MethodDefinition(
                ".cctor",
                MethodAttributes.Private ||| MethodAttributes.Static ||| MethodAttributes.SpecialName ||| MethodAttributes.RTSpecialName,
                ctx.bt.typ_void
                )
        let il = method.Body.GetILProcessor()

        let mem_size_in_pages = 1
        let size_in_bytes = mem_size_in_pages * mem_page_size

        il.Append(il.Create(OpCodes.Ldc_I4, mem_size_in_pages))
        il.Append(il.Create(OpCodes.Stsfld, ctx.mem_size))

        il.Append(il.Create(OpCodes.Ldc_I4, size_in_bytes))
        // TODO where is this freed?
        let ext = ctx.mem.Module.ImportReference(typeof<System.Runtime.InteropServices.Marshal>.GetMethod("AllocHGlobal", [| typeof<int32> |] ))
        il.Append(il.Create(OpCodes.Call, ext))
        il.Append(il.Create(OpCodes.Stsfld, ctx.mem))

        // memset 0
        il.Append(il.Create(OpCodes.Ldsfld, ctx.mem))
        il.Append(il.Create(OpCodes.Ldc_I4, 0))
        il.Append(il.Create(OpCodes.Ldc_I4, size_in_bytes))
        il.Append(il.Create(OpCodes.Initblk))

        match tbl_setup with
        | Some m -> il.Append(il.Create(OpCodes.Call, m))
        | None -> ()

        match data_setup with
        | Some m -> il.Append(il.Create(OpCodes.Call, m))
        | None -> ()

        let f_make_tmp = make_tmp method

        // TODO mv globals out into a different func?
        for g in ctx.a_globals do
            match g with
            | GlobalRefInternal gi -> 
                cecil_expr il ctx f_make_tmp Array.empty gi.glob.item.init
                il.Append(il.Create(OpCodes.Stsfld, gi.field))
            | GlobalRefImported _ -> ()

        il.Append(il.Create(OpCodes.Ret))

        method

    let create_data_resources ctx sd =
        let f i d =
            let name = sprintf "data_%d" i
            // public because
            // (1) env.GetResource is in a separate assembly
            // (2) so tests can load the resource manually
            let flags = ManifestResourceAttributes.Public
            let r = EmbeddedResource(name, flags, d.init)
            { item = d; name = name; resource = r; }
        Array.mapi f sd.datas

    let gen_assembly (env : System.Reflection.Assembly) m assembly_name ns classname (ver : System.Version) (dest : System.IO.Stream) =
        let assembly = 
            AssemblyDefinition.CreateAssembly(
                new AssemblyNameDefinition(
                    assembly_name, 
                    ver
                    ), 
                assembly_name, 
                ModuleKind.Dll
                )

        let main_module = assembly.MainModule

        let bt = 
            {
                typ_i32 = main_module.TypeSystem.Int32
                typ_i64 = main_module.TypeSystem.Int64
                typ_f32 = main_module.TypeSystem.Single
                typ_f64 = main_module.TypeSystem.Double
                typ_void = main_module.TypeSystem.Void
                typ_intptr = main_module.TypeSystem.IntPtr
            }

        let container = 
            new TypeDefinition(
                ns,
                classname,
                TypeAttributes.Class ||| TypeAttributes.Public ||| TypeAttributes.Abstract ||| TypeAttributes.Sealed, 
                main_module.TypeSystem.Object
                )

        main_module.Types.Add(container);

        let ndx = get_module_index m

        let mem_size =
            let f =
                new FieldDefinition(
                    "__mem_size",
                    FieldAttributes.Public ||| FieldAttributes.Static, 
                    main_module.TypeSystem.Int32
                    )
            container.Fields.Add(f)
            f :> FieldReference

        let mem =
            match ndx.MemoryImport with
            | Some mi ->
                import_memory main_module mi env
            | None ->
                let f =
                    new FieldDefinition(
                        "__mem",
                        FieldAttributes.Public ||| FieldAttributes.Static, 
                        main_module.TypeSystem.IntPtr
                        )
                container.Fields.Add(f)
                f :> FieldReference

        let a_globals = create_globals ndx bt main_module env

        for m in a_globals do
            match m with
            | GlobalRefInternal mi -> container.Fields.Add(mi.field)
            | GlobalRefImported _ -> ()

        let a_methods = create_methods ndx bt main_module env

        for m in a_methods do
            match m with
            | MethodRefInternal mi -> container.Methods.Add(mi.method)
            | MethodRefImported mi -> ()

        let tbl =
            new FieldDefinition(
                "__tbl",
                FieldAttributes.Public ||| FieldAttributes.Static, 
                main_module.TypeSystem.IntPtr
                )
        container.Fields.Add(tbl)

        // TODO maybe don't gen this if there is no tbl.
        let tbl_lookup = gen_tbl_lookup ndx bt tbl
        container.Methods.Add(tbl_lookup)

        let types =
            match ndx.Type with
            | Some s -> s.types
            | None -> [| |]

        let ctx =
            {
                types = types
                md = container.Module
                bt = bt
                a_globals = a_globals
                a_methods = a_methods
                mem = mem
                mem_size = mem_size
                tbl_lookup = tbl_lookup
            }

        let tbl_setup =
            match (ndx.Table, ndx.Element) with
            | (Some st, Some se) ->
                let lim = st.tables.[0].limits
                let m = gen_tbl_setup ndx ctx tbl lim se.elems
                container.Methods.Add(m)
                Some m
            | (None, None) ->
                None

        gen_code_for_methods ctx

        let data_setup =
            match ndx.Data with
            | Some sd -> 
                let a_datas = create_data_resources ctx sd
                for d in a_datas do
                    main_module.Resources.Add(d.resource)
                let m = gen_data_setup ndx ctx env a_datas
                container.Methods.Add(m)
                Some m
            | None -> None

        let cctor = gen_cctor ctx tbl_setup data_setup
        container.Methods.Add(cctor)

        assembly.Write(dest);

