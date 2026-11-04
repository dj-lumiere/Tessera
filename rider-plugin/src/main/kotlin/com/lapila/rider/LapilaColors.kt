package com.lapila.rider

import com.intellij.openapi.editor.DefaultLanguageHighlighterColors
import com.intellij.openapi.editor.colors.CodeInsightColors
import com.intellij.openapi.editor.colors.TextAttributesKey
import com.intellij.openapi.fileTypes.PlainSyntaxHighlighter
import com.intellij.openapi.fileTypes.SyntaxHighlighter
import com.intellij.openapi.options.colors.AttributesDescriptor
import com.intellij.openapi.options.colors.ColorDescriptor
import com.intellij.openapi.options.colors.ColorSettingsPage
import com.intellij.platform.lsp.api.customization.LspSemanticTokensSupport
import javax.swing.Icon

/**
 * Lapila's own colors. Each starts as the C# color of its counterpart (record as struct, entity as class, concept as
 * interface, routine as method, SSA value as local variable, module path as namespace, preset as constant, terminator as control-flow
 * keyword), so Lapila reads like C# in any scheme until Settings | Editor | Color Scheme | Lapila says otherwise.
 * Blocks and attributes have no C# counterpart of their own: a block starts pink and an attribute yellow
 * (colorSchemes/).
 */
object LapilaColors {
    val RECORD = key("LAPILA_RECORD", TextAttributesKey.find("ReSharper.STRUCT_IDENTIFIER"))
    val ENTITY = key("LAPILA_ENTITY", DefaultLanguageHighlighterColors.CLASS_NAME)
    val CONCEPT = key("LAPILA_CONCEPT", DefaultLanguageHighlighterColors.INTERFACE_NAME)
    val TYPE_PARAMETER = key("LAPILA_TYPE_PARAMETER", TextAttributesKey.find("ReSharper.TYPE_PARAMETER_IDENTIFIER"))
    val ROUTINE = key("LAPILA_ROUTINE", DefaultLanguageHighlighterColors.INSTANCE_METHOD)
    val BLOCK = key("LAPILA_BLOCK", DefaultLanguageHighlighterColors.LABEL)
    val VALUE = key("LAPILA_VALUE", DefaultLanguageHighlighterColors.LOCAL_VARIABLE)
    val PARAMETER = key("LAPILA_PARAMETER", DefaultLanguageHighlighterColors.PARAMETER)
    val SHARED = key("LAPILA_SHARED", DefaultLanguageHighlighterColors.PARAMETER)
    val FIELD = key("LAPILA_FIELD", DefaultLanguageHighlighterColors.INSTANCE_FIELD)
    val CONSTANT = key("LAPILA_CONSTANT", DefaultLanguageHighlighterColors.CONSTANT)
    val MODULE = key("LAPILA_MODULE", TextAttributesKey.find("ReSharper.NAMESPACE_IDENTIFIER"))
    val ATTRIBUTE = key("LAPILA_ATTRIBUTE", DefaultLanguageHighlighterColors.METADATA)
    val TERMINATOR = key("LAPILA_TERMINATOR", TextAttributesKey.find("ReSharper.CONTROL_FLOW_KEYWORD"))
    val KEYWORD = key("LAPILA_KEYWORD", DefaultLanguageHighlighterColors.KEYWORD)
    val NUMBER = key("LAPILA_NUMBER", DefaultLanguageHighlighterColors.NUMBER)
    val OPERATOR = key("LAPILA_OPERATOR", DefaultLanguageHighlighterColors.OPERATION_SIGN)
    val DOC_TAG = key("LAPILA_DOC_TAG", DefaultLanguageHighlighterColors.DOC_COMMENT_TAG)
    val DOC_VALUE = key("LAPILA_DOC_VALUE", DefaultLanguageHighlighterColors.DOC_COMMENT_TAG_VALUE)

    /** A keyword the next version drops or renames: struck through over its keyword color. */
    val RETIRED = key("LAPILA_RETIRED_KEYWORD", CodeInsightColors.DEPRECATED_ATTRIBUTES)

    private fun key(name: String, csharp: TextAttributesKey) = TextAttributesKey.createTextAttributesKey(name, csharp)
}

/** The colors of the semantic token types the Lapila language server sends. */
internal object LapilaSemanticTokens : LspSemanticTokensSupport() {
    private val keys = mapOf(
        "recordType" to LapilaColors.RECORD,
        "entityType" to LapilaColors.ENTITY,
        "interface" to LapilaColors.CONCEPT,
        "typeParameter" to LapilaColors.TYPE_PARAMETER,
        "function" to LapilaColors.ROUTINE,
        "block" to LapilaColors.BLOCK,
        "variable" to LapilaColors.VALUE,
        "parameter" to LapilaColors.PARAMETER,
        "sharedValue" to LapilaColors.SHARED,
        "property" to LapilaColors.FIELD,
        "constant" to LapilaColors.CONSTANT,
        "namespace" to LapilaColors.MODULE,
        "decorator" to LapilaColors.ATTRIBUTE,
        "controlKeyword" to LapilaColors.TERMINATOR,
        "keyword" to LapilaColors.KEYWORD,
        "number" to LapilaColors.NUMBER,
        "operator" to LapilaColors.OPERATOR,
        "docTag" to LapilaColors.DOC_TAG,
        "docValue" to LapilaColors.DOC_VALUE,
    )

    override val tokenTypes: List<String> = (super.tokenTypes + keys.keys).distinct()

    override fun getTextAttributesKey(tokenType: String, modifiers: List<String>): TextAttributesKey? =
        if ("retired" in modifiers) LapilaColors.RETIRED
        else keys[tokenType] ?: super.getTextAttributesKey(tokenType, modifiers)
}

/** Settings | Editor | Color Scheme | Lapila. */
class LapilaColorSettingsPage : ColorSettingsPage {
    private val descriptors = arrayOf(
        AttributesDescriptor(LapilaBundle.message("color.types.record"), LapilaColors.RECORD),
        AttributesDescriptor(LapilaBundle.message("color.types.entity"), LapilaColors.ENTITY),
        AttributesDescriptor(LapilaBundle.message("color.types.concept"), LapilaColors.CONCEPT),
        AttributesDescriptor(LapilaBundle.message("color.types.typeParameter"), LapilaColors.TYPE_PARAMETER),
        AttributesDescriptor(LapilaBundle.message("color.routine"), LapilaColors.ROUTINE),
        AttributesDescriptor(LapilaBundle.message("color.block"), LapilaColors.BLOCK),
        AttributesDescriptor(LapilaBundle.message("color.values.ssa"), LapilaColors.VALUE),
        AttributesDescriptor(LapilaBundle.message("color.values.parameter"), LapilaColors.PARAMETER),
        AttributesDescriptor(LapilaBundle.message("color.values.shared"), LapilaColors.SHARED),
        AttributesDescriptor(LapilaBundle.message("color.values.field"), LapilaColors.FIELD),
        AttributesDescriptor(LapilaBundle.message("color.values.constant"), LapilaColors.CONSTANT),
        AttributesDescriptor(LapilaBundle.message("color.module"), LapilaColors.MODULE),
        AttributesDescriptor(LapilaBundle.message("color.attribute"), LapilaColors.ATTRIBUTE),
        AttributesDescriptor(LapilaBundle.message("color.keywords.terminator"), LapilaColors.TERMINATOR),
        AttributesDescriptor(LapilaBundle.message("color.keywords.keyword"), LapilaColors.KEYWORD),
        AttributesDescriptor(LapilaBundle.message("color.number"), LapilaColors.NUMBER),
        AttributesDescriptor(LapilaBundle.message("color.operator"), LapilaColors.OPERATOR),
        AttributesDescriptor(LapilaBundle.message("color.doc.tag"), LapilaColors.DOC_TAG),
        AttributesDescriptor(LapilaBundle.message("color.doc.value"), LapilaColors.DOC_VALUE),
    )

    private val tags = mapOf(
        "record" to LapilaColors.RECORD,
        "entity" to LapilaColors.ENTITY,
        "concept" to LapilaColors.CONCEPT,
        "tp" to LapilaColors.TYPE_PARAMETER,
        "routine" to LapilaColors.ROUTINE,
        "block" to LapilaColors.BLOCK,
        "value" to LapilaColors.VALUE,
        "param" to LapilaColors.PARAMETER,
        "shared" to LapilaColors.SHARED,
        "field" to LapilaColors.FIELD,
        "const" to LapilaColors.CONSTANT,
        "module" to LapilaColors.MODULE,
        "attr" to LapilaColors.ATTRIBUTE,
        "term" to LapilaColors.TERMINATOR,
        "kw" to LapilaColors.KEYWORD,
        "num" to LapilaColors.NUMBER,
        "op" to LapilaColors.OPERATOR,
    )

    override fun getDisplayName(): String = "Lapila"

    override fun getIcon(): Icon = LapilaIconProvider.ICON

    override fun getHighlighter(): SyntaxHighlighter = PlainSyntaxHighlighter()

    override fun getAttributeDescriptors(): Array<AttributesDescriptor> = descriptors

    override fun getColorDescriptors(): Array<ColorDescriptor> = ColorDescriptor.EMPTY_ARRAY

    override fun getAdditionalHighlightingTagToDescriptorMap(): Map<String, TextAttributesKey> = tags

    override fun getDemoText(): String = """
        <kw>import</kw> <module>Hosted</module>

        <kw>preset</kw> <const>LIMIT</const>: <record>S64</record> = <num>100</num>

        <kw>record</kw> <record>Point</record>
            <field>x</field>: <record>S64</record>
            <field>y</field>: <record>S64</record>

        <kw>entity</kw> <entity>Ledger</entity>
            <field>total</field>: <record>S64</record>

        <attr>#track_caller</attr>
        <kw>routine</kw> <routine>find</routine><<tp>T</tp>: <concept>Eq</concept>>(<param>data</param>: <op>@</op><tp>T</tp>, <param>length</param>: <record>U64</record>, <param>target</param>: <tp>T</tp>) -> <record>Option</record><<record>U64</record>>
            <kw>shared</kw> <shared>step</shared> : <record>U64</record> = <num>1</num>

            <kw>block</kw> <block>entry</block>()
                <term>jump</term> <block>scan</block>(<num>0</num>)

            <kw>block</kw> <block>scan</block>(<param>i</param>: <record>U64</record>)
                <value>done</value> : <record>Bool</record> = <param>i</param>.<routine>ge</routine>(<param>length</param>)
                <term>branch</term> <value>done</value>
                    ? <term>return</term>(.<const>Absent</const>)
                    : <term>continue</term>
                <term>when</term>
                    <param>data</param>.<routine>stride</routine>(<param>i</param>).<routine>load</routine>().<routine>eq</routine>(<param>target</param>) -> <term>return</term>(.<const>Present</const>(<param>i</param>))
                    <term>else</term> -> <block>scan</block>(<param>i</param>.<routine>add</routine>(<shared>step</shared>))
    """.trimIndent()
}
