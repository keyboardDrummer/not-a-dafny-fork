module {:extern "DAST"} DAST {
  datatype Module = Module(name: string, body: seq<ModuleItem>)

  datatype ModuleItem = Module(Module) | Class(Class) | Trait(Trait) | Newtype(Newtype) | Datatype(Datatype)

  datatype Newtype = Newtype(name: string, base: Type, witnessExpr: Optional<Expression>)

  datatype Type = Path(seq<Ident>, typeArgs: seq<Type>, resolved: ResolvedType) | Tuple(seq<Type>) | Primitive(Primitive) | Passthrough(string) | TypeArg(Ident)

  datatype Primitive = String | Bool | Char

  datatype ResolvedType = Datatype(path: seq<Ident>) | Trait(path: seq<Ident>) | Newtype

  datatype Ident = Ident(id: string)

  datatype Class = Class(name: string, superClasses: seq<Type>, body: seq<ClassItem>)

  datatype Trait = Trait(name: string, typeParams: seq<Type>, body: seq<ClassItem>)

  datatype Datatype = Datatype(name: string, enclosingModule: Ident, typeParams: seq<Type>, ctors: seq<DatatypeCtor>, body: seq<ClassItem>, isCo: bool)

  datatype DatatypeCtor = DatatypeCtor(name: string, args: seq<Formal>, hasAnyArgs: bool /* includes ghost */)

  datatype ClassItem = Method(Method) | Field(Formal)

  datatype Formal = Formal(name: string, typ: Type)

  datatype Method = Method(isStatic: bool, hasBody: bool, overridingPath: Optional<seq<Ident>>, name: string, typeParams: seq<Type>, params: seq<Formal>, body: seq<Statement>, outTypes: seq<Type>, outVars: Optional<seq<Ident>>)

  datatype Optional<T> = Some(T) | None

  datatype Statement =
    DeclareVar(name: string, typ: Type, maybeValue: Optional<Expression>) |
    Assign(name: string, value: Expression) |
    If(cond: Expression, thn: seq<Statement>, els: seq<Statement>) |
    While(cond: Expression, body: seq<Statement>) |
    Call(on: Expression, name: string, typeArgs: seq<Type>, args: seq<Expression>, outs: Optional<seq<Ident>>) |
    Return(expr: Expression) |
    EarlyReturn() |
    Halt() |
    Print(Expression)

  datatype Expression =
    Literal(Literal) |
    Ident(string) |
    Companion(seq<Ident>) |
    Tuple(seq<Expression>) |
    New(path: seq<Ident>, args: seq<Expression>) |
    DatatypeValue(path: seq<Ident>, variant: string, isCo: bool, contents: seq<(string, Expression)>) |
    NewtypeValue(tpe: Type, value: Expression) |
    This() |
    Ite(cond: Expression, thn: Expression, els: Expression) |
    UnOp(unOp: UnaryOp, expr: Expression) |
    BinOp(op: string, left: Expression, right: Expression) |
    Select(expr: Expression, field: string, onDatatype: bool) |
    TupleSelect(expr: Expression, index: nat) |
    Call(on: Expression, name: string, typeArgs: seq<Type>, args: seq<Expression>) |
    TypeTest(on: Expression, dType: seq<Ident>, variant: string) |
    InitializationValue(typ: Type)

  datatype UnaryOp = Not | BitwiseNot | Cardinality

  datatype Literal = BoolLiteral(bool) | IntLiteral(int) | DecLiteral(string) | StringLiteral(string)
}
