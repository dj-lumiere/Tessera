package com.tessera.rider

import com.intellij.openapi.editor.DefaultLanguageHighlighterColors
import com.intellij.openapi.editor.colors.TextAttributesKey
import com.intellij.openapi.fileTypes.PlainSyntaxHighlighter
import com.intellij.openapi.fileTypes.SyntaxHighlighter
import com.intellij.openapi.options.colors.AttributesDescriptor
import com.intellij.openapi.options.colors.ColorDescriptor
import com.intellij.openapi.options.colors.ColorSettingsPage
import com.intellij.platform.lsp.api.customization.LspSemanticTokensSupport
import javax.swing.Icon

/**
 * Tessera's own colors. Each starts as the C# color of its counterpart (record as struct, concept as interface, routine
 * as method, SSA value as local variable, module path as namespace, preset as constant, terminator as control-flow
 * keyword), so Tessera reads like C# in any scheme until Settings | Editor | Color Scheme | Tessera says otherwise.
 * Blocks and attributes have no C# counterpart of their own: a block starts purple and an attribute yellow
 * (colorSchemes/).
 */
object TesseraColors {
    val RECORD = key("TESSERA_RECORD", TextAttributesKey.find("ReSharper.STRUCT_IDENTIFIER"))
    val CONCEPT = key("TESSERA_CONCEPT", DefaultLanguageHighlighterColors.INTERFACE_NAME)
    val TYPE_PARAMETER = key("TESSERA_TYPE_PARAMETER", TextAttributesKey.find("ReSharper.TYPE_PARAMETER_IDENTIFIER"))
    val ROUTINE = key("TESSERA_ROUTINE", DefaultLanguageHighlighterColors.INSTANCE_METHOD)
    val BLOCK = key("TESSERA_BLOCK", DefaultLanguageHighlighterColors.LABEL)
    val VALUE = key("TESSERA_VALUE", DefaultLanguageHighlighterColors.LOCAL_VARIABLE)
    val PARAMETER = key("TESSERA_PARAMETER", DefaultLanguageHighlighterColors.PARAMETER)
    val FIELD = key("TESSERA_FIELD", DefaultLanguageHighlighterColors.INSTANCE_FIELD)
    val CONSTANT = key("TESSERA_CONSTANT", DefaultLanguageHighlighterColors.CONSTANT)
    val MODULE = key("TESSERA_MODULE", TextAttributesKey.find("ReSharper.NAMESPACE_IDENTIFIER"))
    val ATTRIBUTE = key("TESSERA_ATTRIBUTE", DefaultLanguageHighlighterColors.METADATA)
    val TERMINATOR = key("TESSERA_TERMINATOR", TextAttributesKey.find("ReSharper.CONTROL_FLOW_KEYWORD"))
    val KEYWORD = key("TESSERA_KEYWORD", DefaultLanguageHighlighterColors.KEYWORD)
    val NUMBER = key("TESSERA_NUMBER", DefaultLanguageHighlighterColors.NUMBER)
    val OPERATOR = key("TESSERA_OPERATOR", DefaultLanguageHighlighterColors.OPERATION_SIGN)

    private fun key(name: String, csharp: TextAttributesKey) = TextAttributesKey.createTextAttributesKey(name, csharp)
}

/** The colors of the semantic token types the Tessera language server sends. */
internal object TesseraSemanticTokens : LspSemanticTokensSupport() {
    private val keys = mapOf(
        "recordType" to TesseraColors.RECORD,
        "interface" to TesseraColors.CONCEPT,
        "typeParameter" to TesseraColors.TYPE_PARAMETER,
        "function" to TesseraColors.ROUTINE,
        "block" to TesseraColors.BLOCK,
        "variable" to TesseraColors.VALUE,
        "parameter" to TesseraColors.PARAMETER,
        "property" to TesseraColors.FIELD,
        "constant" to TesseraColors.CONSTANT,
        "namespace" to TesseraColors.MODULE,
        "decorator" to TesseraColors.ATTRIBUTE,
        "controlKeyword" to TesseraColors.TERMINATOR,
        "keyword" to TesseraColors.KEYWORD,
        "number" to TesseraColors.NUMBER,
        "operator" to TesseraColors.OPERATOR,
    )

    override val tokenTypes: List<String> = (super.tokenTypes + keys.keys).distinct()

    override fun getTextAttributesKey(tokenType: String, modifiers: List<String>): TextAttributesKey? =
        keys[tokenType] ?: super.getTextAttributesKey(tokenType, modifiers)
}

/** Settings | Editor | Color Scheme | Tessera. */
class TesseraColorSettingsPage : ColorSettingsPage {
    private val descriptors = arrayOf(
        AttributesDescriptor("Types//Record, choice, variant", TesseraColors.RECORD),
        AttributesDescriptor("Types//Concept", TesseraColors.CONCEPT),
        AttributesDescriptor("Types//Type parameter", TesseraColors.TYPE_PARAMETER),
        AttributesDescriptor("Routine", TesseraColors.ROUTINE),
        AttributesDescriptor("Block", TesseraColors.BLOCK),
        AttributesDescriptor("Values//SSA value", TesseraColors.VALUE),
        AttributesDescriptor("Values//Parameter", TesseraColors.PARAMETER),
        AttributesDescriptor("Values//Field", TesseraColors.FIELD),
        AttributesDescriptor("Values//Preset, global, case", TesseraColors.CONSTANT),
        AttributesDescriptor("Module", TesseraColors.MODULE),
        AttributesDescriptor("Attribute", TesseraColors.ATTRIBUTE),
        AttributesDescriptor("Keywords//Terminator", TesseraColors.TERMINATOR),
        AttributesDescriptor("Keywords//Keyword", TesseraColors.KEYWORD),
        AttributesDescriptor("Number", TesseraColors.NUMBER),
        AttributesDescriptor("Operator", TesseraColors.OPERATOR),
    )

    private val tags = mapOf(
        "record" to TesseraColors.RECORD,
        "concept" to TesseraColors.CONCEPT,
        "tp" to TesseraColors.TYPE_PARAMETER,
        "routine" to TesseraColors.ROUTINE,
        "block" to TesseraColors.BLOCK,
        "value" to TesseraColors.VALUE,
        "param" to TesseraColors.PARAMETER,
        "field" to TesseraColors.FIELD,
        "const" to TesseraColors.CONSTANT,
        "module" to TesseraColors.MODULE,
        "attr" to TesseraColors.ATTRIBUTE,
        "term" to TesseraColors.TERMINATOR,
        "kw" to TesseraColors.KEYWORD,
        "num" to TesseraColors.NUMBER,
        "op" to TesseraColors.OPERATOR,
    )

    override fun getDisplayName(): String = "Tessera"

    override fun getIcon(): Icon = TesseraIconProvider.ICON

    override fun getHighlighter(): SyntaxHighlighter = PlainSyntaxHighlighter()

    override fun getAttributeDescriptors(): Array<AttributesDescriptor> = descriptors

    override fun getColorDescriptors(): Array<ColorDescriptor> = ColorDescriptor.EMPTY_ARRAY

    override fun getAdditionalHighlightingTagToDescriptorMap(): Map<String, TextAttributesKey> = tags

    override fun getDemoText(): String = """
        <kw>import</kw> <module>Standard</module>::<module>Os</module>

        <kw>preset</kw> <const>LIMIT</const>: <record>S64</record> = <num>100</num>

        <kw>record</kw> <record>Point</record>
            <field>x</field>: <record>S64</record>
            <field>y</field>: <record>S64</record>

        <attr>#track_caller</attr>
        <kw>routine</kw> <routine>find</routine><<tp>T</tp>: <concept>Eq</concept>>(<param>data</param>: <op>@</op><tp>T</tp>, <param>length</param>: <record>USize</record>, <param>target</param>: <tp>T</tp>) -> <record>Option</record><<record>USize</record>>
            <kw>block</kw> <block>entry</block>()
                <term>jump</term> <block>scan</block>(<num>0</num>)

            <kw>block</kw> <block>scan</block>(<param>i</param>: <record>USize</record>)
                <value>done</value> : <record>Bool</record> = <param>i</param>.<routine>ge</routine>(<param>length</param>)
                <term>branch</term> <value>done</value>
                    ? <term>return</term>(.<const>Absent</const>)
                    : <term>continue</term>
                <term>when</term>
                    <param>data</param>.<routine>stride</routine>(<param>i</param>).<routine>load</routine>().<routine>eq</routine>(<param>target</param>) -> <term>return</term>(.<const>Present</const>(<param>i</param>))
                    <term>else</term> -> <block>scan</block>(<param>i</param>.<routine>add</routine>(<num>1</num>))
    """.trimIndent()
}
