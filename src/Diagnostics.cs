namespace Lapila;

/// The three families of diagnostic codes. Revare, Mielia and Lapila number their diagnostics from one shared space:
/// a number names one diagnostic, and the prefix (LI for Lapila) only follows the file's language.
public enum DiagFamily
{
    /// A source that does not read (the lexer and the parser).
    Grammar = 'G',

    /// A source that reads but is wrong (checks, manifests, command line).
    Semantic = 'S',

    /// A warning or a note: the build goes on.
    Warning = 'W',
}

/// One diagnostic code: its family, its number, a name for the code and one line saying what it reports. `Shared` is
/// true when the number is one Revare and Mielia use for the same check. The numbers a language uses alone start at
/// `OwnFrom`, above everything the shared registry has.
public sealed record Code(DiagFamily Family, int Number, string Name, string Summary, bool Shared)
{
    /// The first number a Lapila-only diagnostic may use.
    public const int OwnFrom = 1000;

    public const string Prefix = "LI";

    public bool IsWarning => Family == DiagFamily.Warning;

    /// `LI-S007`: the prefix, the family letter and the number in at least three digits.
    public string Text => $"{Prefix}-{(char)Family}{Number:D3}";

    /// The opening of a printed diagnostic: `error[LI-S007]` or `warning[LI-W305]`.
    public string Head => IsWarning ? $"warning[{Text}]" : $"error[{Text}]";

    public override string ToString() => Text;
}

/// Every diagnostic code Lapila reports, one field per code. Report sites name the code here and nowhere else spells a
/// number. The table is grouped as the shared numbering is: grammar by the part of the grammar, semantic by the
/// kind of check in blocks of fifty or a hundred, warnings by kind.
public static class Li
{
    private static Code G(int number, string name, string summary, bool shared = false) =>
        new(DiagFamily.Grammar, number, name, summary, shared);

    private static Code S(int number, string name, string summary, bool shared = false) =>
        new(DiagFamily.Semantic, number, name, summary, shared);

    private static Code W(int number, string name, string summary, bool shared = false) =>
        new(DiagFamily.Warning, number, name, summary, shared);

    // Grammar, numbers shared with Revare and Mielia
    public static readonly Code UnterminatedString = G(1, "UnterminatedString", "A string literal is not closed on its line.", shared: true);
    public static readonly Code InvalidCharacter = G(3, "InvalidCharacter", "A character that cannot stand here (outside text and comments a source is ASCII).", shared: true);
    public static readonly Code InvalidNumericLiteral = G(4, "InvalidNumericLiteral", "A number that is malformed: a bad digit for its base, or a stray character in it.", shared: true);
    public static readonly Code InvalidEscape = G(5, "InvalidEscape", "An escape sequence that does not exist or is malformed.", shared: true);
    public static readonly Code InvalidFloatLiteral = G(6, "InvalidFloatLiteral", "A float literal that is malformed.", shared: true);
    public static readonly Code InvalidBacktickName = G(9, "InvalidBacktickName", "A name between backticks that is not closed on its line or is empty.", shared: true);
    public static readonly Code ExpectedExpression = G(112, "ExpectedExpression", "An expression is needed and something else was found.", shared: true);
    public static readonly Code ExpectedDeclaration = G(115, "ExpectedDeclaration", "A declaration is needed at the top of a file and something else was found.", shared: true);
    public static readonly Code UnexpectedToken = G(150, "UnexpectedToken", "A token other than the one the grammar needs here (expected X, found Y).", shared: true);
    public static readonly Code MixedOperatorsNeedParentheses = G(216, "MixedOperatorsNeedParentheses", "and with or, or two different bitwise operators, side by side without parentheses.", shared: true);
    public static readonly Code EachKeyword = G(229, "EachKeyword", "A loop written with each, which is not a keyword.", shared: true);
    public static readonly Code FailableRemoved = G(230, "FailableRemoved", "The word failable, which no longer exists.", shared: true);

    // Grammar, Lapila only
    public static readonly Code InvalidSourceEncoding = G(1000, "InvalidSourceEncoding", "The source file is not valid UTF-8.");
    public static readonly Code BoolOperatorSpelling = G(1001, "BoolOperatorSpelling", "A Bool operator spelled with a symbol (| or & instead of or and and, ! instead of not).");
    public static readonly Code UnmatchedBracket = G(1002, "UnmatchedBracket", "A closing bracket with no opening one.");
    public static readonly Code UnitLiteral = G(1003, "UnitLiteral", "A duration or byte-size literal that is fractional or negative.");
    public static readonly Code ByteLiteral = G(1004, "ByteLiteral", "A byte or character literal of the wrong form.");
    public static readonly Code RemovedSpelling = G(1010, "RemovedSpelling", "A spelling the language no longer has (the message says what to write instead).");
    public static readonly Code KeywordAsName = G(1011, "KeywordAsName", "A keyword used as a name, or a name that may not be used (me).");
    public static readonly Code ModuleLine = G(1020, "ModuleLine", "A module line that is misplaced, repeated, or not in the module of the file's folder.");
    public static readonly Code ModuleName = G(1021, "ModuleName", "A module or define name that is not PascalCase, or a path that starts no known module.");
    public static readonly Code ImportPlacement = G(1022, "ImportPlacement", "A module or import line that is not at the top of the file.");
    public static readonly Code AnnotationArguments = G(1030, "AnnotationArguments", "Annotation arguments of the wrong form (not literals, a bad list, a bad @source).");
    public static readonly Code AnnotationPlacement = G(1031, "AnnotationPlacement", "An annotation on the wrong line or before the wrong thing.");
    public static readonly Code ProtocolClauseSyntax = G(1040, "ProtocolClauseSyntax", "A needs, when or obeys clause written wrongly.");
    public static readonly Code TypeParameterSyntax = G(1041, "TypeParameterSyntax", "A type parameter written wrongly, or a default that is not allowed.");
    public static readonly Code RoutineHeaderSyntax = G(1050, "RoutineHeaderSyntax", "A routine header written wrongly (common, @, me, infallible, a missing body).");
    public static readonly Code BlockSyntax = G(1051, "BlockSyntax", "A block of an assembly or block routine written wrongly (entry, parameters, running off the end).");
    public static readonly Code ChoiceFlagsSyntax = G(1060, "ChoiceFlagsSyntax", "A choice or flags declaration written wrongly (member spelling, bits, no members).");
    public static readonly Code VariantSyntax = G(1061, "VariantSyntax", "A variant declaration written wrongly.");
    public static readonly Code PresetGlobalSyntax = G(1062, "PresetGlobalSyntax", "A preset, global or lateinit declaration written wrongly.");
    public static readonly Code TypeSyntax = G(1063, "TypeSyntax", "A type written wrongly (inline variant, Callable, tuple size, crashable parameters).");
    public static readonly Code StatementSyntax = G(1070, "StatementSyntax", "A statement written wrongly (binding, steal, inject, drop, claim, tuple binding).");
    public static readonly Code ConditionalSyntax = G(1071, "ConditionalSyntax", "An if, else, when or arm written wrongly or with no body.");
    public static readonly Code LoopSyntax = G(1072, "LoopSyntax", "A loop written wrongly (for, until, loop, naming, then).");
    public static readonly Code JumpSyntax = G(1073, "JumpSyntax", "A break, continue, becomes, throw or return written wrongly.");
    public static readonly Code BranchSyntax = G(1074, "BranchSyntax", "A value if or a branch written wrongly.");
    public static readonly Code ComparisonChain = G(1080, "ComparisonChain", "A comparison chain that mixes directions or chains <=>.");
    public static readonly Code ExpressionSyntax = G(1081, "ExpressionSyntax", "An expression written wrongly (list repeat, generic arguments, record literal, trailing comma).");

    // Semantic, numbers shared with Revare and Mielia
    public static readonly Code UnknownName = S(7, "UnknownName", "A name that is not defined here, or is not visible from here.", shared: true);
    public static readonly Code NameBoundTwice = S(9, "NameBoundTwice", "A name bound again where it is already bound.", shared: true);
    public static readonly Code IntegerLiteralRange = S(10, "IntegerLiteralRange", "A number literal outside the range of its type.", shared: true);
    public static readonly Code FloatLiteralOverflow = S(11, "FloatLiteralOverflow", "A number beyond the largest value of its float type.", shared: true);
    public static readonly Code HexFloatNotBinaryFloat = S(17, "HexFloatNotBinaryFloat", "A hex float literal given a type that is not a binary float.", shared: true);
    public static readonly Code HexFloatInexact = S(18, "HexFloatInexact", "A hex float literal that is not exactly the type's value.", shared: true);
    public static readonly Code UnknownType = S(100, "UnknownType", "A type that is not defined.", shared: true);
    public static readonly Code TypeNotGeneric = S(101, "TypeNotGeneric", "Generic arguments given to a type that takes none.", shared: true);
    public static readonly Code WrongTypeArgumentCount = S(102, "WrongTypeArgumentCount", "The wrong number of generic arguments.", shared: true);
    public static readonly Code ImportPosition = S(114, "ImportPosition", "An import or module line after other declarations.", shared: true);
    public static readonly Code ProtocolNotHeld = S(150, "ProtocolNotHeld", "A type does not satisfy a protocol a generic or conformance needs.", shared: true);
    public static readonly Code CannotInferTypeArgument = S(161, "CannotInferTypeArgument", "A type argument that cannot be inferred from the call.", shared: true);
    public static readonly Code BreakOutsideLoop = S(207, "BreakOutsideLoop", "A break with no loop around it.", shared: true);
    public static readonly Code ContinueOutsideLoop = S(208, "ContinueOutsideLoop", "A continue with no loop around it.", shared: true);
    public static readonly Code UnusedReturnValue = S(210, "UnusedReturnValue", "A call whose result is left unused.", shared: true);
    public static readonly Code DestructuringArity = S(220, "DestructuringArity", "A tuple taken apart into the wrong number of names.", shared: true);
    public static readonly Code PresetNotConstant = S(258, "PresetNotConstant", "A value that is not a buildtime constant where one is needed.", shared: true);
    public static readonly Code ReturnTypeMismatch = S(301, "ReturnTypeMismatch", "A return that does not match the routine's return type.", shared: true);
    public static readonly Code VariantBranchNotFound = S(353, "VariantBranchNotFound", "A type that is not a branch of the variant.", shared: true);
    public static readonly Code ChoiceMemberNotFound = S(355, "ChoiceMemberNotFound", "A name that is not a member of the choice.", shared: true);
    public static readonly Code NonExhaustiveWhen = S(356, "NonExhaustiveWhen", "A when that does not cover every case.", shared: true);
    public static readonly Code DuplicateWhenCase = S(358, "DuplicateWhenCase", "Two arms or cases of a when that match the same thing.", shared: true);
    public static readonly Code SecretField = S(403, "SecretField", "A field that is secret to its module, used from outside.", shared: true);
    public static readonly Code DuplicateRoutine = S(406, "DuplicateRoutine", "Two routines with the same signature, or one symbol defined twice.", shared: true);
    public static readonly Code DuplicateField = S(407, "DuplicateField", "A field declared twice.", shared: true);
    public static readonly Code RecordHoldsEntity = S(409, "RecordHoldsEntity", "A record or tuple that holds an entity by value.", shared: true);
    public static readonly Code EntityCopied = S(413, "EntityCopied", "An entity copied instead of reached through a pointer.", shared: true);
    public static readonly Code InvalidDropTarget = S(421, "InvalidDropTarget", "A drop of something that is not a call.", shared: true);
    public static readonly Code EmptyEnumeration = S(433, "EmptyEnumeration", "A variant or flags type with no members.", shared: true);
    public static readonly Code GlobalChangedAcrossThreads = S(448, "GlobalChangedAcrossThreads", "A routine run on another thread uses a global that changes.", shared: true);
    public static readonly Code FieldGivenTwice = S(452, "FieldGivenTwice", "A field given twice in a literal.", shared: true);
    public static readonly Code FieldNotFound = S(453, "FieldNotFound", "A record with no such field.", shared: true);
    public static readonly Code MissingField = S(455, "MissingField", "A literal that leaves out a field.", shared: true);
    public static readonly Code MethodNotFound = S(458, "MethodNotFound", "A type with no such routine or method.", shared: true);
    public static readonly Code TooFewArguments = S(500, "TooFewArguments", "A call with arguments missing.", shared: true);
    public static readonly Code TooManyArguments = S(501, "TooManyArguments", "A call with more arguments than the routine takes.", shared: true);
    public static readonly Code ArgumentTypeMismatch = S(502, "ArgumentTypeMismatch", "An argument whose type does not match its parameter.", shared: true);
    public static readonly Code UnknownNamedArgument = S(505, "UnknownNamedArgument", "An argument label that names no parameter.", shared: true);
    public static readonly Code DuplicateNamedArgument = S(506, "DuplicateNamedArgument", "An argument given twice.", shared: true);
    public static readonly Code NamedArgumentRequired = S(510, "NamedArgumentRequired", "An argument that is the name of another parameter, in the wrong place, and needs its label.", shared: true);
    public static readonly Code RangeStepZero = S(516, "RangeStepZero", "A range with a step of 0.", shared: true);
    public static readonly Code EmitterRunOut = S(517, "EmitterRunOut", "A for loop over an emitter that an earlier loop already used up.", shared: true);
    public static readonly Code ViewReturned = S(600, "ViewReturned", "A view returned from the routine that owns the memory it shows.", shared: true);
    public static readonly Code ViewOutlives = S(601, "ViewOutlives", "A view kept where it outlives the memory it shows.", shared: true);
    public static readonly Code ThrowNotCrashable = S(700, "ThrowNotCrashable", "A throw of something that is not a crashable.", shared: true);
    public static readonly Code MissingProtocolRoutine = S(702, "MissingProtocolRoutine", "A conformance whose routine does not exist.", shared: true);
    public static readonly Code ProtocolRoutineMismatch = S(703, "ProtocolRoutineMismatch", "A routine that does not have the shape the protocol wants.", shared: true);
    public static readonly Code ThrowsListIncomplete = S(712, "ThrowsListIncomplete", "A @throws list that leaves out a crashable the routine can throw.", shared: true);
    public static readonly Code ThrowsListMisplaced = S(713, "ThrowsListMisplaced", "A @throws list on a routine that cannot fail, or naming a type that is not a crashable.", shared: true);
    public static readonly Code UnreachableStatement = S(786, "UnreachableStatement", "A line that can never run.", shared: true);
    public static readonly Code ExternalSignatureMismatch = S(811, "ExternalSignatureMismatch", "Two declarations of one C symbol that disagree.", shared: true);
    public static readonly Code RecursiveValueRecord = S(960, "RecursiveValueRecord", "A type that contains itself by value.", shared: true);

    // Semantic, Lapila only
    public static readonly Code ModuleCollision = S(1000, "ModuleCollision", "A module of the program that has the path of a standard library module.");
    public static readonly Code NameInOtherModule = S(1001, "NameInOtherModule", "A name that exists in a module this file does not import, or on the other side of a target.");
    public static readonly Code AmbiguousName = S(1002, "AmbiguousName", "A name defined in more than one module or place.");
    public static readonly Code DefineProblem = S(1003, "DefineProblem", "A define that names neither a module nor a type, or clashes with a type.");
    public static readonly Code DeclaredTwice = S(1004, "DeclaredTwice", "A name declared twice in one scope.");
    public static readonly Code NoMatchingOverload = S(1010, "NoMatchingOverload", "No overload of a routine takes these arguments.");
    public static readonly Code AmbiguousOverload = S(1011, "AmbiguousOverload", "More than one overload takes these arguments, or an overload needs its type to be named.");
    public static readonly Code OverloadSymbolClash = S(1012, "OverloadSymbolClash", "Overloads that share one C symbol, or an overloaded @export without a symbol.");
    public static readonly Code EntryPoint = S(1020, "EntryPoint", "The program's start, script or when_power_on routine is missing, duplicated or the wrong shape.");
    public static readonly Code BuilderCalledShape = S(1021, "BuilderCalledShape", "A routine the builder calls that does not have the shape it calls.");
    public static readonly Code TypeArgument = S(1030, "TypeArgument", "A generic argument that is the wrong kind (a type for a number, a choice member for a type).");
    public static readonly Code TypeSpelling = S(1031, "TypeSpelling", "A type written with a name the language does not use (Void, Unit, Mapping, a renamed type).");
    public static readonly Code TypeForm = S(1032, "TypeForm", "A built-in type written with the wrong arguments (Items, Vector, Check, Callable, pointers).");
    public static readonly Code ChoiceUnderlying = S(1033, "ChoiceUnderlying", "A choice whose type is not an integer type.");
    public static readonly Code FlagsDeclaration = S(1034, "FlagsDeclaration", "A flags type with a member outside the bits of its type or two members on one bit.");
    public static readonly Code TupleSize = S(1035, "TupleSize", "A tuple with too few or too many items.");
    public static readonly Code UnionBuild = S(1037, "UnionBuild", "A variant the builder cannot build because the library's union is missing.");
    public static readonly Code AnnotationArgument = S(1050, "AnnotationArgument", "An annotation given arguments it does not take.");
    public static readonly Code AnnotationTarget = S(1051, "AnnotationTarget", "An annotation on a declaration it does not belong on, or two annotations that cannot go together.");
    public static readonly Code ExportProblem = S(1052, "ExportProblem", "An @export that cannot be done (generic, unreadable return type, a name C cannot use).");
    public static readonly Code ExternalProblem = S(1053, "ExternalProblem", "An @external declaration of the wrong form.");
    public static readonly Code ThreadLocalProblem = S(1054, "ThreadLocalProblem", "A @threadlocal on something that cannot be one.");
    public static readonly Code LayoutProblem = S(1055, "LayoutProblem", "A @layout or @aligned that cannot be done.");
    public static readonly Code ReshapingProblem = S(1056, "ReshapingProblem", "A @reshaping that names the wrong thing or a routine that moves memory the caller holds.");
    public static readonly Code TemplateProblem = S(1057, "TemplateProblem", "A @template with an unknown or unfinished placeholder.");
    public static readonly Code BuildSettingAnnotation = S(1058, "BuildSettingAnnotation", "A @target, @feature, @heap_check, @race_check or @trace written wrongly.");
    public static readonly Code TrackCallerProblem = S(1059, "TrackCallerProblem", "A @track_caller or caller_location() used where it cannot be.");
    public static readonly Code ReleaseDutyDeclared = S(1060, "ReleaseDutyDeclared", "A type that holds something to release without saying how it is released.");
    public static readonly Code UnavailableRoutine = S(1061, "UnavailableRoutine", "A routine marked @unavailable or @refused called on a target it does not run on.");
    public static readonly Code BuildErrorCall = S(1062, "BuildErrorCall", "A build_error call that is not given one text.");
    public static readonly Code LibraryBuildError = S(1063, "LibraryBuildError", "A build error a library routine raises with build_error.");
    public static readonly Code BuildtimeFold = S(1070, "BuildtimeFold", "A call that cannot run at build time, in a preset.");
    public static readonly Code PresetValue = S(1071, "PresetValue", "A preset whose value or type cannot be a preset (an entity, an address, itself).");
    public static readonly Code BuildtimeArithmetic = S(1072, "BuildtimeArithmetic", "A buildtime calculation that divides by zero, overflows or does not fit.");
    public static readonly Code LiteralNeedsType = S(1073, "LiteralNeedsType", "A number literal that nothing gives a type.");
    public static readonly Code GlobalInitializer = S(1074, "GlobalInitializer", "A global or preset initializer of the wrong form.");
    public static readonly Code StringLiteralSize = S(1075, "StringLiteralSize", "A string literal that does not fit the array or the character width it fills.");
    public static readonly Code AddressLiteral = S(1076, "AddressLiteral", "An address literal that is malformed or too wide for the target.");
    public static readonly Code ProtocolReference = S(1090, "ProtocolReference", "A protocol that is unknown or given the wrong number of arguments.");
    public static readonly Code RecordKindMisfit = S(1091, "RecordKindMisfit", "A type of the wrong kind for a protocol's record or entity requirement.");
    public static readonly Code DeriveProblem = S(1092, "DeriveProblem", "A derive that cannot be done for this type.");
    public static readonly Code OperatorProtocol = S(1093, "OperatorProtocol", "An operator used on a type that does not obey the protocol behind it.");
    public static readonly Code StealMisuse = S(1100, "StealMisuse", "A steal or inject that would leave a place hollow or take from the wrong place.");
    public static readonly Code HollowRead = S(1101, "HollowRead", "A value that may hold nothing, used without looking at it with when.");
    public static readonly Code ManagerFill = S(1102, "ManagerFill", "A place filled from a manager the wrong way.");
    public static readonly Code BorrowForm = S(1103, "BorrowForm", "A borrow written wrongly (<-, from STACK, &T forms).");
    public static readonly Code GlobalMemoryOverwritten = S(1104, "GlobalMemoryOverwritten", "A global that holds memory, overwritten or never given back.");
    public static readonly Code NeverGivenBack = S(1105, "NeverGivenBack", "A value with something to give back that the routine never gives back.");
    public static readonly Code ReadOnlyWrite = S(1106, "ReadOnlyWrite", "A write into memory that is read-only (a preset, a text literal, a view of one).");
    public static readonly Code SharedAccessWrite = S(1107, "SharedAccessWrite", "A write or a reshaping through a shared read access.");
    public static readonly Code ThreadLocalAddress = S(1108, "ThreadLocalAddress", "The address of a thread-local variable kept or handed to another thread.");
    public static readonly Code CrossThreadView = S(1109, "CrossThreadView", "A view handed to work that runs on another thread.");
    public static readonly Code MemoryCheckLimit = S(1110, "MemoryCheckLimit", "A routine with too many paths for the memory check to follow.");
    public static readonly Code LateinitRead = S(1111, "LateinitRead", "A lateinit place read before it is filled.");
    public static readonly Code NothingToGiveBack = S(1113, "NothingToGiveBack", "A drop of a value that has nothing to give back.");
    public static readonly Code ChannelSharing = S(1114, "ChannelSharing", "A channel that hands out a value it cannot hand over safely.");
    public static readonly Code AccessHeldTwice = S(1115, "AccessHeldTwice", "Access to something taken again while it is already held.");
    public static readonly Code GivenBackTwice = S(1116, "GivenBackTwice", "Memory or access given back twice, or a value that ended already ended again.");
    public static readonly Code UsedWhileLent = S(1117, "UsedWhileLent", "A value used or changed while a piece of work holds it.");
    public static readonly Code ViewUsedAfterEnd = S(1118, "ViewUsedAfterEnd", "A view used after the memory it shows was given back or moved.");
    public static readonly Code HollowUsed = S(1119, "HollowUsed", "A place left hollow and then read, taken or returned.");
    public static readonly Code WorkForm = S(1120, "WorkForm", "A to[Work] written on something that is not a routine, or an ambiguous one.");
    public static readonly Code WorkArguments = S(1121, "WorkArguments", "The arguments of to[Work] that do not match the routine's parameters.");
    public static readonly Code WorkRunsOnce = S(1122, "WorkRunsOnce", "A piece of work that runs once and is used twice.");
    public static readonly Code WorkFailures = S(1123, "WorkFailures", "A work that can throw something its ticket does not take.");
    public static readonly Code FailureAcrossC = S(1130, "FailureAcrossC", "A failure that has to cross to C, which cannot receive one.");
    public static readonly Code FailureForm = S(1131, "FailureForm", "A throw, put_failure or throw_failure_from written wrongly.");
    public static readonly Code FailureSize = S(1132, "FailureSize", "A crashable that does not fit the room a failure has.");
    public static readonly Code FailureSettle = S(1133, "FailureSettle", "The failures of routines that call each other do not settle.");
    public static readonly Code FailureMemory = S(1134, "FailureMemory", "A failure that takes memory from a general memory manager.");
    public static readonly Code FailureCallable = S(1135, "FailureCallable", "A routine value that may fail given where a plain one is needed.");
    public static readonly Code ThrowsAnnotation = S(1136, "ThrowsAnnotation", "A @throws list that is not a list of crashables.");
    public static readonly Code MemoryNotGivenBack = S(1140, "MemoryNotGivenBack", "Memory that is overwritten or left behind without being given back.");
    public static readonly Code WhenArms = S(1150, "WhenArms", "The arms of a when are in the wrong order or repeat (else twice, else first, else that never runs).");
    public static readonly Code WhenSubject = S(1151, "WhenSubject", "A when over a value it cannot take (several flags on at once, a type with no branches).");
    public static readonly Code WhenCase = S(1152, "WhenCase", "A when case that is not a constant integer, preset or choice member.");
    public static readonly Code WhenValueArm = S(1153, "WhenValueArm", "An arm of a when that gives a value ends without one.");
    public static readonly Code WhenObservation = S(1154, "WhenObservation", "A when that observes a variant, an optional place or a failure the wrong way.");
    public static readonly Code JumpArguments = S(1155, "JumpArguments", "A jump or branch with the wrong arguments.");
    public static readonly Code BlockDeclaration = S(1156, "BlockDeclaration", "A block declared twice or with a clashing name or parameter.");
    public static readonly Code LoopCarried = S(1157, "LoopCarried", "A loop that carries values and does not pass them on.");
    public static readonly Code LoopSource = S(1158, "LoopSource", "A for loop over something it cannot walk.");
    public static readonly Code LoopName = S(1159, "LoopName", "A break or continue that names no loop around it.");
    public static readonly Code ReturnForm = S(1160, "ReturnForm", "A return that does not match the routine (noreturn, None, an empty return).");
    public static readonly Code DuplicateParameter = S(1161, "DuplicateParameter", "A parameter declared twice.");
    public static readonly Code BecomesOutsideArm = S(1162, "BecomesOutsideArm", "A becomes that is not in an arm of a when that gives a value.");
    public static readonly Code OperatorUse = S(1180, "OperatorUse", "An operator used on types it does not take (/ on integers, comparing addresses).");
    public static readonly Code FlagsUse = S(1181, "FlagsUse", "A flags value used with an operator or a number it does not take.");
    public static readonly Code FieldAccess = S(1182, "FieldAccess", "A field read on something that has no fields, or through a pointer.");
    public static readonly Code StrideUse = S(1183, "StrideUse", "A stride on something that is not a pointer, or with a wrong index type.");
    public static readonly Code PointerUse = S(1184, "PointerUse", "A value used as a pointer that is not one, or a claim of a wrong type.");
    public static readonly Code LambdaCapture = S(1185, "LambdaCapture", "A lambda that uses a value of the routine around it.");
    public static readonly Code TypeNeeded = S(1186, "TypeNeeded", "A place where the type cannot be inferred and must be written.");
    public static readonly Code TypeMismatch = S(1187, "TypeMismatch", "A value of one type where another is expected.");
    public static readonly Code RoutineValue = S(1188, "RoutineValue", "A routine used as a value (Callable) that cannot be one.");
    public static readonly Code LambdaShape = S(1189, "LambdaShape", "A lambda that does not fit the Callable it becomes.");
    public static readonly Code RemovedMethod = S(1190, "RemovedMethod", "A method that no longer exists (the message says what replaces it).");
    public static readonly Code CallTypeArguments = S(1191, "CallTypeArguments", "Type arguments of a call that are wrong or too many.");
    public static readonly Code ConsoleCall = S(1192, "ConsoleCall", "A Console call written wrongly.");
    public static readonly Code WriteTemplate = S(1193, "WriteTemplate", "A write string with an unbalanced brace or an empty one.");
    public static readonly Code DangerousConversion = S(1194, "DangerousConversion", "A conversion (inttoptr, bitcast) the build refuses without care.");
    public static readonly Code MissingValue = S(1195, "MissingValue", "A routine value, global or binding that has no value where one is needed.");
    public static readonly Code ShowNotAvailable = S(1196, "ShowNotAvailable", "A value shown with a routine its type does not have.");
    public static readonly Code TypeNotSupported = S(1198, "TypeNotSupported", "A type that cannot be used here (C ABI by value, generic precheck, pointer rules).");
    public static readonly Code TargetCpuProblem = S(1199, "TargetCpuProblem", "The target's CPU or features cannot be resolved.");
    public static readonly Code AsmRegisterType = S(1200, "AsmRegisterType", "An assembly operand or register whose type does not fit the register.");
    public static readonly Code AsmRegisterClash = S(1201, "AsmRegisterClash", "Two values in one register, or a register that cannot be named here.");
    public static readonly Code AsmNaked = S(1202, "AsmNaked", "A @naked routine that takes or returns something registers cannot carry.");
    public static readonly Code AsmRoutineForm = S(1203, "AsmRoutineForm", "An assembly routine of the wrong form (type parameters, annotations, architecture).");
    public static readonly Code AsmBlock = S(1204, "AsmBlock", "An assembly block written wrongly (names, jumps, ends).");
    public static readonly Code AsmInstruction = S(1205, "AsmInstruction", "An instruction written wrongly or with the wrong operands.");
    public static readonly Code AsmMemoryOperand = S(1206, "AsmMemoryOperand", "A memory operand written wrongly or not available on this architecture.");
    public static readonly Code AsmCondition = S(1207, "AsmCondition", "The condition of an if in an assembly block written wrongly.");
    public static readonly Code AsmClobbers = S(1208, "AsmClobbers", "A @clobbers list that names what it cannot.");
    public static readonly Code AsmReturn = S(1209, "AsmReturn", "The return of an assembly routine that does not match its result.");
    public static readonly Code ManifestSyntax = S(1400, "ManifestSyntax", "A config.toml that is not valid TOML.");
    public static readonly Code ManifestUnknownKey = S(1401, "ManifestUnknownKey", "A section or key the manifest does not have.");
    public static readonly Code ManifestMissing = S(1402, "ManifestMissing", "A section or key the manifest needs.");
    public static readonly Code ManifestValue = S(1403, "ManifestValue", "A manifest value of the wrong type or outside its range.");
    public static readonly Code ManifestTarget = S(1404, "ManifestTarget", "A [target] that does not fit its board, mode or operating system.");
    public static readonly Code ManifestPath = S(1405, "ManifestPath", "A file or directory the manifest names that does not exist.");
    public static readonly Code ManifestRenamed = S(1406, "ManifestRenamed", "A manifest key that was renamed or removed (the message names the new one).");
    public static readonly Code BoardFile = S(1410, "BoardFile", "A board file that is wrong (unknown board, memory map, devices).");
    public static readonly Code NoSuchFile = S(1450, "NoSuchFile", "An input file or directory that does not exist.");
    public static readonly Code BadOption = S(1451, "BadOption", "A command line option that is unknown, missing its value, or belongs in config.toml.");
    public static readonly Code TargetMismatch = S(1452, "TargetMismatch", "A request for a target the board or this machine cannot do.");
    public static readonly Code ToolchainMissing = S(1453, "ToolchainMissing", "A tool the build needs (clang, the standard library) that was not found or failed.");
    public static readonly Code OldExtension = S(1454, "OldExtension", "A source file that still has an old extension, .te or .li.");
    public static readonly Code CommandUsage = S(1455, "CommandUsage", "A command called without what it needs.");

    // Warning, numbers shared with Revare and Mielia
    public static readonly Code UnusedLoopName = W(10, "UnusedLoopName", "A loop named but not used by any break or continue.", shared: true);
    public static readonly Code EmptyLoopBody = W(57, "EmptyLoopBody", "A loop whose body never runs (until true).", shared: true);
    public static readonly Code TodoLeft = W(303, "TodoLeft", "A routine that still has todo blocks.", shared: true);
    public static readonly Code EndlessUntil = W(304, "EndlessUntil", "An until false that never ends by itself.", shared: true);
    public static readonly Code NoDocComment = W(305, "NoDocComment", "A routine with no doc comment.", shared: true);
    public static readonly Code DocLacksParam = W(306, "DocLacksParam", "A doc comment with no :param line for a parameter.", shared: true);
    public static readonly Code DocUnknownParam = W(307, "DocUnknownParam", "A :param line naming no parameter.", shared: true);
    public static readonly Code DocLacksReturns = W(308, "DocLacksReturns", "A doc comment with no :returns: line for a routine that returns a value.", shared: true);

    // Warning, Lapila only
    public static readonly Code ChainTooLong = W(1000, "ChainTooLong", "A chain of calls on one line with too many calls.");
    public static readonly Code ReturnInFinishBlock = W(1001, "ReturnInFinishBlock", "A return inside a block that should end with a jump.");
    public static readonly Code TooManyParameters = W(1002, "TooManyParameters", "A routine with so many parameters that a record should carry them.");
    public static readonly Code MemoryReleasedOnce = W(1003, "MemoryReleasedOnce", "Two names for the same memory, which is released once.");
    public static readonly Code ShowsCell = W(1004, "ShowsCell", "A value shown as the cell itself instead of what it holds.");
    public static readonly Code ExpensiveCallInLoop = W(1005, "ExpensiveCallInLoop", "A call to a routine marked #expensive on a loop, or a loop-carried cost.");

    /// All codes, in the order of the fields.
    public static IReadOnlyList<Code> All { get; } = typeof(Li)
        .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
        .Where(f => f.FieldType == typeof(Code))
        .Select(f => (Code)f.GetValue(null)!)
        .ToList();
}
