using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using moddingSuite.Model.Ndfbin;
using moddingSuite.Model.Ndfbin.Types;
using moddingSuite.Model.Ndfbin.Types.AllTypes;

namespace moddingSuite.BL.Ndf
{
    public sealed class DivisionCanonicalScriptWriter
    {
        private readonly LocalisationTokenResolver _tokenResolver;

        public DivisionCanonicalScriptWriter(LocalisationTokenResolver tokenResolver)
        {
            _tokenResolver = tokenResolver ?? throw new ArgumentNullException(nameof(tokenResolver));
        }

        public string CreateStrictScript(NdfBinary ndfBinary, DivisionTemplateMatchResult matchResult)
        {
            if (ndfBinary == null)
                throw new ArgumentNullException(nameof(ndfBinary));

            if (matchResult == null || !matchResult.Success)
                throw new ArgumentException("A successful template match is required.", nameof(matchResult));

            var runtimeDescriptors = new Dictionary<string, NdfObject>(matchResult.RuntimeDescriptorsByGuid, StringComparer.OrdinalIgnoreCase);
            var descriptorOrder = matchResult.MatchedKnowledgeFile.Descriptors.OrderBy(x => x.OrderInFile).ToList();

            var output = new StringBuilder(1024 * 256);
            foreach (string line in matchResult.MatchedKnowledgeFile.PreludeLines)
            {
                output.Append(line);
                output.Append("\r\n");
            }

            if (descriptorOrder.Count > 0 && matchResult.MatchedKnowledgeFile.PreludeLines.Count > 0)
                output.Append("\r\n");

            for (int index = 0; index < descriptorOrder.Count; index++)
            {
                DivisionDescriptorKnowledge descriptorKnowledge = descriptorOrder[index];
                NdfObject runtimeDescriptor;
                if (!runtimeDescriptors.TryGetValue(descriptorKnowledge.DescriptorGuid, out runtimeDescriptor))
                {
                    throw new InvalidOperationException(
                        string.Format("Runtime descriptor for GUID {0} is missing in strict mode.", descriptorKnowledge.DescriptorGuid));
                }

                AppendDescriptorBlock(output, runtimeDescriptor, descriptorKnowledge);
                if (index < descriptorOrder.Count - 1)
                    output.Append("\r\n");
            }

            return output.ToString();
        }

        private void AppendDescriptorBlock(StringBuilder output, NdfObject runtimeDescriptor, DivisionDescriptorKnowledge descriptorKnowledge)
        {
            output.Append("export ").Append(descriptorKnowledge.ExportName).Append(" is TDeckDivisionDescriptor\r\n");
            output.Append("(\r\n");

            foreach (string fieldName in GetFieldOrder(descriptorKnowledge))
            {
                string literal = ResolveFieldLiteral(fieldName, runtimeDescriptor, descriptorKnowledge);
                output.Append("    ").Append(fieldName).Append(" = ").Append(literal).Append("\r\n");
            }

            output.Append(")\r\n");
        }

        private static IEnumerable<string> GetFieldOrder(DivisionDescriptorKnowledge descriptorKnowledge)
        {
            if (descriptorKnowledge.FieldOrder != null && descriptorKnowledge.FieldOrder.Count > 0)
                return descriptorKnowledge.FieldOrder;

            string[] fallback = new[]
            {
                "DescriptorId",
                "CfgName",
                "DivisionName",
                "InterfaceOrder",
                "DivisionPowerClassification",
                "DivisionCoalition",
                "DivisionTags",
                "DescriptionHintTitleToken",
                "MaxActivationPoints",
                "DivisionRule",
                "CostMatrix",
                "EmblemTexture",
                "StrategicLabelColor",
                "PortraitTexture",
                "TypeTexture",
                "CountryId"
            };

            var result = new List<string>();
            foreach (string field in fallback)
            {
                string _;
                if (descriptorKnowledge.TryGetField(field, out _))
                    result.Add(field);
            }

            foreach (string key in descriptorKnowledge.Fields.Keys)
            {
                if (!result.Contains(key, StringComparer.OrdinalIgnoreCase))
                    result.Add(key);
            }

            return result;
        }

        private string ResolveFieldLiteral(string fieldName, NdfObject runtimeDescriptor, DivisionDescriptorKnowledge descriptorKnowledge)
        {
            if (string.IsNullOrWhiteSpace(fieldName))
                throw new InvalidOperationException("Descriptor template contains an empty field name.");

            switch (fieldName)
            {
                case "DescriptorId":
                    return string.Format("GUID:{{{0}}}", GetDescriptorGuidLiteral(runtimeDescriptor, descriptorKnowledge));

                case "CfgName":
                case "DivisionRule":
                case "CostMatrix":
                    return GetRequiredTemplateField(descriptorKnowledge, fieldName);

                case "DivisionName":
                case "DivisionPowerClassification":
                case "DescriptionHintTitleToken":
                    return ResolveLocalisationLiteral(runtimeDescriptor, descriptorKnowledge, fieldName);

                case "InterfaceOrder":
                    return GetInterfaceOrderLiteral(runtimeDescriptor, descriptorKnowledge);

                case "DivisionCoalition":
                    return GetCoalitionLiteral(runtimeDescriptor, descriptorKnowledge);

                case "DivisionTags":
                    return GetDivisionTagsLiteral(runtimeDescriptor, descriptorKnowledge);

                case "MaxActivationPoints":
                    return GetMaxActivationPointsLiteral(runtimeDescriptor, descriptorKnowledge);

                case "EmblemTexture":
                case "StrategicLabelColor":
                case "PortraitTexture":
                case "TypeTexture":
                case "CountryId":
                    return GetQuotedStringLiteral(runtimeDescriptor, descriptorKnowledge, fieldName);

                default:
                    return GetRequiredTemplateField(descriptorKnowledge, fieldName);
            }
        }

        private string ResolveLocalisationLiteral(NdfObject descriptor, DivisionDescriptorKnowledge knowledge, string propertyName)
        {
            string templateLiteral;
            knowledge.TryGetField(propertyName, out templateLiteral);

            NdfPropertyValue property;
            if (!TryGetProperty(descriptor, propertyName, out property))
            {
                if (!string.IsNullOrWhiteSpace(templateLiteral))
                    return templateLiteral.Trim();

                throw new InvalidOperationException(
                    string.Format("Runtime descriptor {0} does not have required property '{1}'.", descriptor.Id, propertyName));
            }

            var localisationHash = property.Value as NdfLocalisationHash;
            if (localisationHash == null)
            {
                if (!string.IsNullOrWhiteSpace(templateLiteral))
                    return templateLiteral.Trim();

                throw new InvalidOperationException(string.Format("Property '{0}' is not a localisation hash.", propertyName));
            }

            return _tokenResolver.ResolveStrict(localisationHash.Value, templateLiteral, propertyName);
        }

        private static string GetDescriptorGuidLiteral(NdfObject descriptor, DivisionDescriptorKnowledge knowledge)
        {
            string runtimeGuid;
            if (TryGetGuid(descriptor, "DescriptorId", out runtimeGuid))
                return NdfScriptGuidNormalizer.NormalizeGuidForScript(runtimeGuid);

            if (!string.IsNullOrWhiteSpace(knowledge.DescriptorGuid))
                return knowledge.DescriptorGuid.Trim();

            throw new InvalidOperationException(
                string.Format("Runtime descriptor {0} does not have required property 'DescriptorId'.", descriptor.Id));
        }

        private static string GetInterfaceOrderLiteral(NdfObject descriptor, DivisionDescriptorKnowledge knowledge)
        {
            double value;
            if (TryGetNumericValue(descriptor, "InterfaceOrder", out value))
                return FormatInterfaceOrder(value);

            return GetRequiredTemplateField(knowledge, "InterfaceOrder");
        }

        private static string GetCoalitionLiteral(NdfObject descriptor, DivisionDescriptorKnowledge knowledge)
        {
            int coalitionValue;
            if (TryGetInteger(descriptor, "DivisionCoalition", out coalitionValue))
                return FormatCoalition(coalitionValue);

            return GetRequiredTemplateField(knowledge, "DivisionCoalition");
        }

        private static string GetDivisionTagsLiteral(NdfObject descriptor, DivisionDescriptorKnowledge knowledge)
        {
            List<string> values;
            if (TryGetStringList(descriptor, "DivisionTags", out values))
                return FormatStringList(values, true);

            return GetRequiredTemplateField(knowledge, "DivisionTags");
        }

        private static string GetMaxActivationPointsLiteral(NdfObject descriptor, DivisionDescriptorKnowledge knowledge)
        {
            int value;
            if (TryGetInteger(descriptor, "MaxActivationPoints", out value))
                return value.ToString(CultureInfo.InvariantCulture);

            return GetRequiredTemplateField(knowledge, "MaxActivationPoints");
        }

        private static string GetQuotedStringLiteral(NdfObject descriptor, DivisionDescriptorKnowledge knowledge, string propertyName)
        {
            string runtimeValue;
            if (TryGetString(descriptor, propertyName, out runtimeValue))
                return QuoteWithDoubleQuotes(runtimeValue);

            return GetRequiredTemplateField(knowledge, propertyName);
        }

        private static string GetRequiredTemplateField(DivisionDescriptorKnowledge descriptorKnowledge, string fieldName)
        {
            string value;
            if (!descriptorKnowledge.TryGetField(fieldName, out value) || string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException(
                    string.Format("Template field '{0}' is missing for {1}.", fieldName, descriptorKnowledge.ExportName));

            return value.Trim();
        }

        private static NdfPropertyValue GetRequiredProperty(NdfObject descriptor, string propertyName)
        {
            NdfPropertyValue property;
            if (!TryGetProperty(descriptor, propertyName, out property))
            {
                throw new InvalidOperationException(
                    string.Format("Runtime descriptor {0} does not have required property '{1}'.", descriptor.Id, propertyName));
            }

            return property;
        }

        private static bool TryGetProperty(NdfObject descriptor, string propertyName, out NdfPropertyValue property)
        {
            property = descriptor.PropertyValues
                .FirstOrDefault(x => x.Property != null && x.Property.Name == propertyName && x.Type != NdfType.Unset && x.Value != null);

            return property != null;
        }

        private static bool TryGetGuid(NdfObject descriptor, string propertyName, out string guidText)
        {
            guidText = null;
            NdfPropertyValue property;
            if (!TryGetProperty(descriptor, propertyName, out property))
                return false;

            var guidWrapper = property.Value as NdfGuid;
            if (guidWrapper == null || guidWrapper.Value == null)
                return false;

            guidText = guidWrapper.Value.ToString();
            return true;
        }

        private static bool TryGetNumericValue(NdfObject descriptor, string propertyName, out double value)
        {
            value = 0;
            NdfPropertyValue property;
            if (!TryGetProperty(descriptor, propertyName, out property))
                return false;

            var flat = property.Value as NdfFlatValueWrapper;
            if (flat == null || flat.Value == null)
                return false;

            try
            {
                value = Convert.ToDouble(flat.Value, CultureInfo.InvariantCulture);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetInteger(NdfObject descriptor, string propertyName, out int value)
        {
            value = 0;
            double numericValue;
            if (!TryGetNumericValue(descriptor, propertyName, out numericValue))
                return false;

            value = Convert.ToInt32(Math.Round(numericValue), CultureInfo.InvariantCulture);
            return true;
        }

        private static string GetRequiredGuid(NdfObject descriptor, string propertyName)
        {
            NdfPropertyValue property = GetRequiredProperty(descriptor, propertyName);
            var guidWrapper = property.Value as NdfGuid;
            if (guidWrapper == null || guidWrapper.Value == null)
                throw new InvalidOperationException(string.Format("Property '{0}' is not a GUID.", propertyName));

            return guidWrapper.Value.ToString();
        }

        private static double GetRequiredNumericValue(NdfObject descriptor, string propertyName)
        {
            NdfPropertyValue property = GetRequiredProperty(descriptor, propertyName);
            var flat = property.Value as NdfFlatValueWrapper;
            if (flat == null || flat.Value == null)
                throw new InvalidOperationException(string.Format("Property '{0}' is not numeric.", propertyName));

            try
            {
                return Convert.ToDouble(flat.Value, CultureInfo.InvariantCulture);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(string.Format("Property '{0}' cannot be converted to numeric value.", propertyName), ex);
            }
        }

        private static int GetRequiredInteger(NdfObject descriptor, string propertyName)
        {
            double value = GetRequiredNumericValue(descriptor, propertyName);
            return Convert.ToInt32(Math.Round(value), CultureInfo.InvariantCulture);
        }

        private static string FormatInterfaceOrder(double value)
        {
            double rounded = Math.Round(value);
            if (Math.Abs(value - rounded) < 0.0000001)
                return rounded.ToString("0.0", CultureInfo.InvariantCulture);

            return value.ToString("0.0################", CultureInfo.InvariantCulture);
        }

        private static string FormatCoalition(int coalitionValue)
        {
            switch (coalitionValue)
            {
                case 1:
                    return "ECoalition/NATO";
                case 2:
                    return "ECoalition/PACT";
                default:
                    throw new InvalidOperationException(
                        string.Format("Unsupported coalition value '{0}' in strict mode.", coalitionValue));
            }
        }

        private static List<string> GetRequiredStringList(NdfObject descriptor, string propertyName)
        {
            NdfPropertyValue property = GetRequiredProperty(descriptor, propertyName);
            var list = property.Value as NdfCollection;
            if (list == null)
                throw new InvalidOperationException(string.Format("Property '{0}' is not a list.", propertyName));

            var result = new List<string>();
            foreach (CollectionItemValueHolder item in list)
            {
                if (item == null || item.Value == null)
                    continue;

                result.Add(GetStringFromValue(item.Value, propertyName));
            }

            return result;
        }

        private static string GetRequiredString(NdfObject descriptor, string propertyName)
        {
            NdfPropertyValue property = GetRequiredProperty(descriptor, propertyName);
            return GetStringFromValue(property.Value, propertyName);
        }

        private static bool TryGetString(NdfObject descriptor, string propertyName, out string value)
        {
            value = null;
            NdfPropertyValue property;
            if (!TryGetProperty(descriptor, propertyName, out property))
                return false;

            try
            {
                value = GetStringFromValue(property.Value, propertyName);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetStringList(NdfObject descriptor, string propertyName, out List<string> values)
        {
            values = null;
            NdfPropertyValue property;
            if (!TryGetProperty(descriptor, propertyName, out property))
                return false;

            var list = property.Value as NdfCollection;
            if (list == null)
                return false;

            var result = new List<string>();
            foreach (CollectionItemValueHolder item in list)
            {
                if (item == null || item.Value == null)
                    continue;

                try
                {
                    result.Add(GetStringFromValue(item.Value, propertyName));
                }
                catch
                {
                    return false;
                }
            }

            values = result;
            return true;
        }

        private static string GetStringFromValue(NdfValueWrapper value, string propertyName)
        {
            var flat = value as NdfFlatValueWrapper;
            if (flat == null || flat.Value == null)
                throw new InvalidOperationException(string.Format("Property '{0}' is not a string value.", propertyName));

            var stringReference = flat.Value as NdfStringReference;
            if (stringReference != null)
                return stringReference.Value;

            var transReference = flat.Value as NdfTranReference;
            if (transReference != null)
                return transReference.Value;

            string asText = flat.Value as string;
            if (asText != null)
                return asText;

            throw new InvalidOperationException(
                string.Format("Property '{0}' uses unsupported string wrapper type {1}.", propertyName, flat.Value.GetType().Name));
        }

        private static string FormatStringList(IEnumerable<string> values, bool singleQuotedItems)
        {
            if (values == null)
                return "[]";

            var items = values.Where(x => x != null).ToList();
            if (items.Count == 0)
                return "[]";

            var formatted = singleQuotedItems
                ? items.Select(QuoteWithSingleQuotes)
                : items.Select(QuoteWithDoubleQuotes);

            return string.Format("[{0}]", string.Join(", ", formatted));
        }

        private static string QuoteWithSingleQuotes(string text)
        {
            string escaped = text.Replace("'", "\\'");
            return string.Format("'{0}'", escaped);
        }

        private static string QuoteWithDoubleQuotes(string text)
        {
            string escaped = text.Replace("\\", "\\\\").Replace("\"", "\\\"");
            return string.Format("\"{0}\"", escaped);
        }
    }
}
