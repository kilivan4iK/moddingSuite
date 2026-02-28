using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using moddingSuite.BL.Ndf;
using moddingSuite.Util;

namespace moddingSuite.Test
{
    [TestClass]
    public class DivisionStrictDecompileSupportTests
    {
        [TestMethod]
        public void GuidNormalizer_ConvertsRuntimeGuidToScriptGuid()
        {
            var runtimeGuid = new Guid("2a326572-9339-094b-9f2c-d775bcc7e14e");

            string scriptGuid = NdfScriptGuidNormalizer.NormalizeGuidForScript(runtimeGuid);

            scriptGuid.Should().Be("7265322a-3993-4b09-9f2c-d775bcc7e14e");
        }

        [TestMethod]
        public void KnowledgeIndex_LoadsDivisionDescriptorsAndLocalisationTokens()
        {
            string tempDirectory = Path.Combine(Path.GetTempPath(), "moddingSuite_division_index_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);

            try
            {
                string ndfPath = Path.Combine(tempDirectory, "Divisions.ndf");
                File.WriteAllText(
                    ndfPath,
                    "export Descriptor_Deck_Division_BEL_16e_Mecanisee_multi is TDeckDivisionDescriptor\r\n" +
                    "(\r\n" +
                    "    DescriptorId = GUID:{7265322a-3993-4b09-9f2c-d775bcc7e14e}\r\n" +
                    "    CfgName = 'BEL_16e_Mecanisee_multi'\r\n" +
                    "    DivisionName = 'DCPLBIYTNN'\r\n" +
                    "    DivisionRule = Descriptor_Deck_Division_BEL_16e_Mecanisee_multi_Rule\r\n" +
                    "    CostMatrix = MatrixCostName_BEL_16e_Mecanisee_multi\r\n" +
                    ")\r\n");

                WarnoNdfKnowledgeIndex index = WarnoNdfKnowledgeIndex.Build(tempDirectory, null);

                index.Files.Count.Should().Be(1);
                index.Files[0].Descriptors.Count.Should().Be(1);
                index.Files[0].Descriptors[0].ExportName.Should().Be("Descriptor_Deck_Division_BEL_16e_Mecanisee_multi");
                index.Files[0].Descriptors[0].DescriptorGuid.Should().Be("7265322a-3993-4b09-9f2c-d775bcc7e14e");

                string token = "DCPLBIYTNN";
                string hashHex = Utils.ByteArrayToBigEndianHexByteString(Utils.CreateLocalisationHash(token, token.Length)).ToUpperInvariant();
                index.TokensByHash.ContainsKey(hashHex).Should().BeTrue();
                index.TokensByHash[hashHex].Contains(token).Should().BeTrue();
            }
            finally
            {
                if (Directory.Exists(tempDirectory))
                    Directory.Delete(tempDirectory, true);
            }
        }
    }
}
