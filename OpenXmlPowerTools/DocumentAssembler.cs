﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;
using System.Xml.Schema;
using DocumentFormat.OpenXml.Packaging;
using OpenXmlPowerTools;
using System.Collections;

namespace OpenXmlPowerTools
{
    public class DocumentAssembler
    {
        public static WmlDocument AssembleDocument(WmlDocument templateDoc, XmlDocument data, out bool templateError)
        {
            XDocument xDoc = data.GetXDocument();
            return AssembleDocument(templateDoc, xDoc.Root, out templateError);
        }

        public static WmlDocument AssembleDocument(WmlDocument templateDoc, XElement data, out bool templateError)
        {
            byte[] byteArray = templateDoc.DocumentByteArray;
            using (MemoryStream mem = new MemoryStream())
            {
                mem.Write(byteArray, 0, (int)byteArray.Length);
                using (WordprocessingDocument wordDoc = WordprocessingDocument.Open(mem, true))
                {
                    if (RevisionAccepter.HasTrackedRevisions(wordDoc))
                        throw new OpenXmlPowerToolsException("Invalid DocumentAssembler template - contains tracked revisions");

                    var te = new TemplateError();
                    foreach (var part in wordDoc.ContentParts())
                    {
                        ProcessTemplatePart(data, te, part);
                    }
                    templateError = te.HasError;
                }
                WmlDocument assembledDocument = new WmlDocument("TempFileName.docx", mem.ToArray());
                return assembledDocument;
            }
        }

        private static void ProcessTemplatePart(XElement data, TemplateError te, OpenXmlPart part)
        {
            XDocument xDoc = part.GetXDocument();

            XElement newRootElementWithMetadata = (XElement)TransformToMetadata(xDoc.Root, data, te);

            NormalizeTablesRepeatAndConditional(newRootElementWithMetadata, te);
            XElement newRootElement = newRootElementWithMetadata;

            // do the actual content replacement
            newRootElement = (XElement)ContentReplacementTransform(newRootElement, data, te);

            xDoc.Elements().First().ReplaceWith(newRootElement);
            part.PutXDocument();
            return;
        }

        // The following method is written using tree modification, not RPFT, because it is easier to write in this fashion.
        // These types of operations are not as easy to write using RPFT.
        // Unless you are completely clear on the semantics of LINQ to XML DML, do not make modifications to this method.
        private static void NormalizeTablesRepeatAndConditional(XElement xDoc, TemplateError te)
        {
            var tables = xDoc.Descendants(PA.Table).ToList();
            foreach (var table in tables)
            {
                var followingElement = table.ElementsAfterSelf().Where(e => e.Name == W.tbl || e.Name == W.p).FirstOrDefault();
                if (followingElement == null || followingElement.Name != W.tbl)
                {
                    table.ReplaceWith(CreateParaErrorMessage("Table metadata is not immediately followed by a table", te));
                    continue;
                }
                // remove superflous paragraph from Table metadata
                table.RemoveNodes();
                // detach w:tbl from parent, and add to Table metadata
                followingElement.Remove();
                table.Add(followingElement);
            }

            int repeatDepth = 0;
            int conditionalDepth = 0;
            foreach (var metadata in xDoc.Descendants().Where(d =>
                    d.Name == PA.Repeat ||
                    d.Name == PA.Conditional ||
                    d.Name == PA.EndRepeat ||
                    d.Name == PA.EndConditional))
            {
                if (metadata.Name == PA.Repeat)
                {
                    ++repeatDepth;
                    metadata.Add(new XAttribute(PA.Depth, repeatDepth));
                    continue;
                }
                if (metadata.Name == PA.EndRepeat)
                {
                    metadata.Add(new XAttribute(PA.Depth, repeatDepth));
                    --repeatDepth;
                    continue;
                }
                if (metadata.Name == PA.Conditional)
                {
                    ++conditionalDepth;
                    metadata.Add(new XAttribute(PA.Depth, conditionalDepth));
                    continue;
                }
                if (metadata.Name == PA.EndConditional)
                {
                    metadata.Add(new XAttribute(PA.Depth, conditionalDepth));
                    --conditionalDepth;
                    continue;
                }
            }

            while (true)
            {
                bool didReplace = false;
                foreach (var metadata in xDoc.Descendants().Where(d => (d.Name == PA.Repeat || d.Name == PA.Conditional) && d.Attribute(PA.Depth) != null).ToList())
                {
                    var depth = (int)metadata.Attribute(PA.Depth);
                    XName matchingEndName = null;
                    if (metadata.Name == PA.Repeat)
                        matchingEndName = PA.EndRepeat;
                    else if (metadata.Name == PA.Conditional)
                        matchingEndName = PA.EndConditional;
                    if (matchingEndName == null)
                        throw new OpenXmlPowerToolsException("Internal error");
                    var matchingEnd = metadata.ElementsAfterSelf(matchingEndName).FirstOrDefault(end => { return (int)end.Attribute(PA.Depth) == depth; });
                    if (matchingEnd == null)
                    {
                        metadata.ReplaceWith(CreateParaErrorMessage(string.Format("{0} does not have matching {1}", metadata.Name.LocalName, matchingEndName.LocalName), te));
                        continue;
                    }
                    metadata.RemoveNodes();
                    var contentBetween = metadata.ElementsAfterSelf().TakeWhile(after => after != matchingEnd).ToList();
                    contentBetween.DescendantsAndSelf(W.bookmarkStart).Remove();
                    contentBetween.DescendantsAndSelf(W.bookmarkEnd).Remove();
                    foreach (var item in contentBetween)
                        item.Remove();
                    metadata.Add(contentBetween);
                    metadata.Attributes(PA.Depth).Remove();
                    matchingEnd.Remove();
                    didReplace = true;
                    break;
                }
                if (!didReplace)
                    break;
            }
        }

        private static object TransformToMetadata(XNode node, XElement data, TemplateError te)
        {
            XElement element = node as XElement;
            if (element != null)
            {
                if (element.Name == W.p)
                {
                    var paraContents = element
                        .DescendantsTrimmed(W.txbxContent)
                        .Where(e => e.Name == W.t)
                        .Select(t => (string)t)
                        .StringConcatenate()
                        .Trim();
                    int occurances = paraContents.Select((c, i) => paraContents.Substring(i)).Count(sub => sub.StartsWith("<#"));
                    if (paraContents.StartsWith("<#") && paraContents.EndsWith("#>") && occurances == 1)
                    {
                        var xmlText = paraContents.Substring(2, paraContents.Length - 4).Trim();
                        XElement xml;
                        try
                        {
                            xml = XElement.Parse(xmlText);
                        }
                        catch (XmlException e)
                        {
                            return CreateParaErrorMessage("XmlException: " + e.Message, te);
                        }
                        string schemaError = ValidatePerSchema(xml);
                        if (schemaError != null)
                            return CreateParaErrorMessage("Schema Validation Error: " + schemaError, te);
                        xml.Add(element);
                        return xml;
                    }
                    if (paraContents.Contains("<#"))
                    {
                        List<RunReplacementInfo> runReplacementInfo = new List<RunReplacementInfo>();
                        var thisGuid = Guid.NewGuid().ToString();
                        var r = new Regex("<#.*?#>");
                        XElement xml = null;
                        OpenXmlRegex.Replace(new[] { element }, r, thisGuid, (para, match) =>
                        {
                            var matchString = match.Value.Trim();
                            var xmlText = matchString.Substring(2, matchString.Length - 4).Trim().Replace('“', '"').Replace('”', '"');
                            try
                            {
                                xml = XElement.Parse(xmlText);
                            }
                            catch (XmlException e)
                            {
                                RunReplacementInfo rri = new RunReplacementInfo()
                                {
                                    Xml = null,
                                    XmlExceptionMessage = "XmlException: " + e.Message,
                                    SchemaValidationMessage = null,
                                };
                                runReplacementInfo.Add(rri);
                                return true;
                            }
                            string schemaError = ValidatePerSchema(xml);
                            if (schemaError != null)
                            {
                                RunReplacementInfo rri = new RunReplacementInfo()
                                {
                                    Xml = null,
                                    XmlExceptionMessage = null,
                                    SchemaValidationMessage = "Schema Validation Error: " + schemaError,
                                };
                                runReplacementInfo.Add(rri);
                                return true;
                            }
                            RunReplacementInfo rri2 = new RunReplacementInfo()
                            {
                                Xml = xml,
                                XmlExceptionMessage = null,
                                SchemaValidationMessage = null,
                            };
                            runReplacementInfo.Add(rri2);
                            return true;
                        }, false);

                        var newPara = new XElement(element);
                        foreach (var rri in runReplacementInfo)
                        {
                            var runToReplace = newPara.Descendants(W.r).FirstOrDefault(rn => rn.Value == thisGuid && rn.Parent.Name != PA.Content);
                            if (runToReplace == null)
                                throw new OpenXmlPowerToolsException("Internal error");
                            if (rri.XmlExceptionMessage != null)
                                runToReplace.ReplaceWith(CreateRunErrorMessage(rri.XmlExceptionMessage, te));
                            else if (rri.SchemaValidationMessage != null)
                                runToReplace.ReplaceWith(CreateRunErrorMessage(rri.SchemaValidationMessage, te));
                            else
                            {
                                var newXml = new XElement(rri.Xml);
                                newXml.Add(runToReplace);
                                runToReplace.ReplaceWith(newXml);
                            }
                        }
                        var coalescedParagraph = WordprocessingMLUtil.CoalesceAdjacentRunsWithIdenticalFormatting(newPara);
                        return coalescedParagraph;
                    }
                }

                return new XElement(element.Name,
                    element.Attributes(),
                    element.Nodes().Select(n => TransformToMetadata(n, data, te)));
            }
            return node;
        }

        private class RunReplacementInfo
        {
            public XElement Xml;
            public string XmlExceptionMessage;
            public string SchemaValidationMessage;
        }

        private static string ValidatePerSchema(XElement element)
        {
            if (s_PASchemaSets == null)
            {
                s_PASchemaSets = new Dictionary<XName, PASchemaSet>()
                {
                    {
                        PA.Content,
                        new PASchemaSet() {
                            XsdMarkup =
                              @"<xs:schema attributeFormDefault='unqualified' elementFormDefault='qualified' xmlns:xs='http://www.w3.org/2001/XMLSchema'>
                                  <xs:element name='Content'>
                                    <xs:complexType>
                                      <xs:attribute name='Select' type='xs:string' use='required' />
                                      <xs:attribute name='Optional' type='xs:boolean' use='optional' />
                                    </xs:complexType>
                                  </xs:element>
                                </xs:schema>",
                        }
                    },
                    {
                        PA.Table,
                        new PASchemaSet() {
                            XsdMarkup =
                              @"<xs:schema attributeFormDefault='unqualified' elementFormDefault='qualified' xmlns:xs='http://www.w3.org/2001/XMLSchema'>
                                  <xs:element name='Table'>
                                    <xs:complexType>
                                      <xs:attribute name='Select' type='xs:string' use='required' />
                                    </xs:complexType>
                                  </xs:element>
                                </xs:schema>",
                        }
                    },
                    {
                        PA.Repeat,
                        new PASchemaSet() {
                            XsdMarkup =
                              @"<xs:schema attributeFormDefault='unqualified' elementFormDefault='qualified' xmlns:xs='http://www.w3.org/2001/XMLSchema'>
                                  <xs:element name='Repeat'>
                                    <xs:complexType>
                                      <xs:attribute name='Select' type='xs:string' use='required' />
                                    </xs:complexType>
                                  </xs:element>
                                </xs:schema>",
                        }
                    },
                    {
                        PA.EndRepeat,
                        new PASchemaSet() {
                            XsdMarkup =
                              @"<xs:schema attributeFormDefault='unqualified' elementFormDefault='qualified' xmlns:xs='http://www.w3.org/2001/XMLSchema'>
                                  <xs:element name='EndRepeat' />
                                </xs:schema>",
                        }
                    },
                    {
                        PA.Conditional,
                        new PASchemaSet() {
                            XsdMarkup =
                              @"<xs:schema attributeFormDefault='unqualified' elementFormDefault='qualified' xmlns:xs='http://www.w3.org/2001/XMLSchema'>
                                  <xs:element name='Conditional'>
                                    <xs:complexType>
                                      <xs:attribute name='Select' type='xs:string' use='required' />
                                      <xs:attribute name='Match' type='xs:string' use='required' />
                                    </xs:complexType>
                                  </xs:element>
                                </xs:schema>",
                        }
                    },
                    {
                        PA.EndConditional,
                        new PASchemaSet() {
                            XsdMarkup =
                              @"<xs:schema attributeFormDefault='unqualified' elementFormDefault='qualified' xmlns:xs='http://www.w3.org/2001/XMLSchema'>
                                  <xs:element name='EndConditional' />
                                </xs:schema>",
                        }
                    },
                };
                foreach (var item in s_PASchemaSets)
                {
                    var itemPAss = item.Value;
                    XmlSchemaSet schemas = new XmlSchemaSet();
                    schemas.Add("", XmlReader.Create(new StringReader(itemPAss.XsdMarkup)));
                    itemPAss.SchemaSet = schemas;
                }
            }
            if (!s_PASchemaSets.ContainsKey(element.Name))
            {
                return string.Format("Invalid XML: {0} is not a valid element", element.Name.LocalName);
            }
            var paSchemaSet = s_PASchemaSets[element.Name];
            XDocument d = new XDocument(element);
            string message = null;
            d.Validate(paSchemaSet.SchemaSet, (sender, e) =>
            {
                if (message == null)
                    message = e.Message;
            }, true);
            if (message != null)
                return message;
            return null;
        }

        private class PA
        {
            public static XName Content = "Content";
            public static XName Table = "Table";
            public static XName Repeat = "Repeat";
            public static XName EndRepeat = "EndRepeat";
            public static XName Conditional = "Conditional";
            public static XName EndConditional = "EndConditional";

            public static XName Select = "Select";
            public static XName Optional = "Optional";
            public static XName Match = "Match";
            public static XName Depth = "Depth";
        }

        private class PASchemaSet
        {
            public string XsdMarkup;
            public XmlSchemaSet SchemaSet;
        }

        private static Dictionary<XName, PASchemaSet> s_PASchemaSets = null;

        private class TemplateError
        {
            public bool HasError = false;
        }

        static object ContentReplacementTransform(XNode node, XElement data, TemplateError templateError)
        {
            XElement element = node as XElement;
            if (element != null)
            {
                if (element.Name == PA.Content)
                {
                    XElement para = element.Element(W.p);
                    XElement run = element.Element(W.r);

                    IEnumerable<XObject> selectedData;
                    string xPath = (string)element.Attribute(PA.Select);
                    try
                    {
                        selectedData = ((IEnumerable)data.XPathEvaluate(xPath)).Cast<XObject>();
                    }
                    catch (XPathException e)
                    {
                        var errorRun = CreateRunErrorMessage("XPathException: " + e.Message, templateError);
                        if (para != null)
                            return new XElement(W.p, errorRun);
                        else
                            return errorRun;
                    }
                    if (!selectedData.Any())
                    {
                        var optionalString = (string)element.Attribute(PA.Optional);
                        if (optionalString != null && optionalString.ToLower() == "true")
                        {
                            return null;
                        }
                        else
                        {
                            var errorRun = CreateRunErrorMessage(string.Format("Content XPath expression ({0}) returned no results", xPath), templateError);
                            if (para != null)
                                return new XElement(W.p, errorRun);
                            else
                                return errorRun;
                        }
                    }
                    else if (selectedData.Count() > 1)
                    {
                        var errorRun = CreateRunErrorMessage(string.Format("Content XPath expression ({0}) returned more than one node", xPath), templateError);
                        if (para != null)
                            return new XElement(W.p, errorRun);
                        else
                            return errorRun;
                    }
                    else
                    {
                        string newValue = null;
                        XObject selectedDatum = selectedData.First();
                        if (selectedDatum is XElement)
                            newValue = ((XElement)selectedDatum).Value;
                        else if (selectedDatum is XAttribute)
                            newValue = ((XAttribute)selectedDatum).Value;

                        if (para != null)
                        {
                            return new XElement(W.p,
                                para.Elements(W.pPr),
                                new XElement(W.r,
                                    para.Elements(W.r).Elements(W.rPr).FirstOrDefault(),
                                    new XElement(W.t, newValue)));
                        }
                        else
                        {
                            return new XElement(W.r,
                                run.Elements().Where(e => e.Name != W.t),
                                new XElement(W.t, newValue));
                        }
                    }
                }
                if (element.Name == PA.Repeat)
                {
                    string selector = (string)element.Attribute(PA.Select);
                    IEnumerable<XElement> repeatingData;
                    try
                    {
                        repeatingData = data.XPathSelectElements(selector);
                    }
                    catch (XPathException e)
                    {
                        return CreateParaErrorMessage("XPathException: " + e.Message, templateError);
                    }
                    if (!repeatingData.Any())
                        return CreateParaErrorMessage("Repeat: Select returned no data", templateError);
                    var newContent = repeatingData.Select(d =>
                        {
                            var content = element
                                .Elements()
                                .Select(e => ContentReplacementTransform(e, d, templateError))
                                .ToList();
                            return content;
                        })
                        .ToList();
                    return newContent;
                }
                if (element.Name == PA.Table)
                {
                    IEnumerable<XElement> tableData;
                    try
                    {
                        tableData = data.XPathSelectElements((string)element.Attribute(PA.Select));
                    }
                    catch (XPathException e)
                    {
                        return CreateParaErrorMessage("XPathException: " + e.Message, templateError);
                    }
                    if (tableData.Count() == 0)
                        return CreateParaErrorMessage("Table Select returned no data", templateError);
                    XElement table = element.Element(W.tbl);
                    XElement protoRow = table.Elements(W.tr).Skip(1).FirstOrDefault();
                    if (protoRow == null)
                        return CreateParaErrorMessage(string.Format("Table does not contain a prototype row"), templateError);
                    protoRow.Descendants(W.bookmarkStart).Remove();
                    protoRow.Descendants(W.bookmarkEnd).Remove();
                    XElement newTable = new XElement(W.tbl,
                        table.Elements().Where(e => e.Name != W.tr),
                        table.Elements(W.tr).FirstOrDefault(),
                        tableData.Select(d =>
                            new XElement(W.tr,
                                protoRow.Elements().Where(r => r.Name != W.tc),
                                protoRow.Elements(W.tc)
                                    .Select(tc =>
                                    {
                                        XElement paragraph = tc.Elements(W.p).FirstOrDefault();
                                        XElement cellRun = paragraph.Elements(W.r).FirstOrDefault();
                                        string xPath = paragraph.Value;
                                        IEnumerable<XObject> selectedData;
                                        try
                                        {
                                            selectedData = ((IEnumerable)d.XPathEvaluate(xPath)).Cast<XObject>();
                                        }
                                        catch (XPathException e)
                                        {
                                            XElement errorCell = new XElement(W.tc,
                                                tc.Elements().Where(z => z.Name != W.p),
                                                new XElement(W.p,
                                                    paragraph.Element(W.pPr),
                                                    CreateRunErrorMessage("XPathException: " + e.Message, templateError)));
                                            return errorCell;
                                        }

	                                    if (!selectedData.Any())
	                                    {
			                                var errorRun = CreateRunErrorMessage(string.Format("XPath expression ({0}) returned no results", xPath), templateError);
                                            XElement errorCell = new XElement(W.tc,
                                                tc.Elements().Where(z => z.Name != W.p),
                                                new XElement(W.p,
                                                    paragraph.Element(W.pPr),
                                                    errorRun));
                                            return errorCell;
	                                    }
                                        else if (selectedData.Count() > 1)
                                        {
                                            var errorRun = CreateRunErrorMessage(string.Format("XPath expression ({0}) returned more than one node", xPath), templateError);
                                            XElement errorCell = new XElement(W.tc,
                                                tc.Elements().Where(z => z.Name != W.p),
                                                new XElement(W.p,
                                                    paragraph.Element(W.pPr),
                                                    errorRun));
                                            return errorCell;
                                        }
                                        else
                                        {
                                            string newValue = null;
                                            XObject selectedDatum = selectedData.First();
                                            if (selectedDatum is XElement)
                                                newValue = ((XElement)selectedDatum).Value;
                                            else if (selectedDatum is XAttribute)
                                                newValue = ((XAttribute)selectedDatum).Value;

                                            XElement newCell = new XElement(W.tc,
                                                tc.Elements().Where(z => z.Name != W.p),
                                                new XElement(W.p,
                                                    paragraph.Element(W.pPr),
                                                    new XElement(W.r,
                                                        cellRun.Element(W.rPr),
                                                        new XElement(W.t, newValue))));
                                            return newCell;
                                        }
                                    }))));
                    return newTable;
                }
                if (element.Name == PA.Conditional)
                {
                    IEnumerable<XObject> selectedData;
                    string xPath = (string)element.Attribute(PA.Select);
	                try
	                {
		                selectedData = ((IEnumerable)data.XPathEvaluate(xPath)).Cast<XObject>();
	                }
                    catch (XPathException e)
                    {
                        return CreateParaErrorMessage("XPathException: " + e.Message, templateError);
                    }
                    if (!selectedData.Any())
                    {
                        return CreateParaErrorMessage(string.Format("Conditional XPath expression ({0}) returned no results", xPath), templateError);
                    }
                    else if (selectedData.Count() > 1)
                    {
                        return CreateParaErrorMessage(string.Format("Conditional XPath expression ({0}) returned more than one node", xPath), templateError);
                    }
                    var match = (string)element.Attribute(PA.Match);
                    string testValue = null;
                    XObject selectedDatum = selectedData.First();
                    if (selectedDatum is XElement)
                        testValue = ((XElement)selectedDatum).Value;
                    else if (selectedDatum is XAttribute)
                        testValue = ((XAttribute)selectedDatum).Value;
                    if (testValue == match)
                    {
                        var content = element.Elements().Select(e => ContentReplacementTransform(e, data, templateError));
                        return content;
                    }
                    else
                        return null;
                }
                return new XElement(element.Name,
                    element.Attributes(),
                    element.Nodes().Select(n => ContentReplacementTransform(n, data, templateError)));
            }
            return node;
        }

        private static XElement CreateRunErrorMessage(string errorMessage, TemplateError templateError)
        {
            templateError.HasError = true;
            var errorRun = new XElement(W.r,
                new XElement(W.rPr,
                    new XElement(W.color, new XAttribute(W.val, "FF0000")),
                    new XElement(W.highlight, new XAttribute(W.val, "yellow"))),
                    new XElement(W.t, errorMessage));
            return errorRun;
        }

        private static XElement CreateParaErrorMessage(string errorMessage, TemplateError templateError)
        {
            templateError.HasError = true;
            var errorPara = new XElement(W.p,
                new XElement(W.r,
                    new XElement(W.rPr,
                        new XElement(W.color, new XAttribute(W.val, "FF0000")),
                        new XElement(W.highlight, new XAttribute(W.val, "yellow"))),
                        new XElement(W.t, errorMessage)));
            return errorPara;
        }
    }
}
