using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using NUnit.Framework;
using YARG.Integration.Link.Session;
using YARG.Integration.Link.Transport;

namespace YARG.Link.Tests
{
    /// <summary>
    /// The C# copies of the constants in <c>Native/YargLink/include/yarg_link.h</c> have the same values as the
    /// header. C# cannot read the header, so it has its own copies; this test makes sure that they agree. The Rust
    /// test <c>header_constants_match</c> checks the header against the Rust constants, so all three agree.
    /// </summary>
    public class HeaderConstantsTests
    {
        private static Dictionary<string, long> ReadHeader([CallerFilePath] string thisFile = "")
        {
            string header = Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..",
                "Native", "YargLink", "include", "yarg_link.h");
            var defines = new Dictionary<string, long>();
            var table = new DataTable();
            foreach (Match match in Regex.Matches(File.ReadAllText(header),
                @"(?m)^#define\s+(YL_[A-Z_]+)\s+([-0-9()*+ ]+?)\s*(?:/\*.*)?$"))
            {
                // The values are integers or simple integer expressions, for example (1024 * 1024 - 1)
                defines[match.Groups[1].Value] = Convert.ToInt64(table.Compute(match.Groups[2].Value, null));
            }
            return defines;
        }

        [Test]
        public void CSharpCopiesMatchTheHeader()
        {
            var header = ReadHeader();
            var copies = new (string Name, long Value)[]
            {
                ("YL_ABI_VERSION", NativeLinkTransport.ABI_VERSION),
                ("YL_MAX_UNRELIABLE", NativeLinkTransport.MAX_UNRELIABLE_SIZE),
                ("YL_MAX_RELIABLE", NativeLinkTransport.MAX_RELIABLE_SIZE),
                ("YL_MAX_RELIABLE", LinkLimits.MAX_MESSAGE_BYTES),
                ("YL_OK", NativeLinkTransport.YL_OK),
                ("YL_ERR_BAD_TICKET", NativeLinkTransport.YL_ERR_BAD_TICKET),
                ("YL_ERR_BUFFER_TOO_SMALL", NativeLinkTransport.YL_ERR_BUFFER_TOO_SMALL),
                ("YL_ERR_TOO_LARGE", NativeLinkTransport.YL_ERR_TOO_LARGE),
                ("YL_CHANNEL_RELIABLE", (long) Delivery.Reliable),
                ("YL_CHANNEL_UNRELIABLE", (long) Delivery.Unreliable),
                ("YL_EVENT_PEER_CONNECTED", NativeLinkTransport.EVENT_PEER_CONNECTED),
                ("YL_EVENT_PEER_DISCONNECTED", NativeLinkTransport.EVENT_PEER_DISCONNECTED),
                ("YL_EVENT_MESSAGE", NativeLinkTransport.EVENT_MESSAGE),
                ("YL_EVENT_ERROR", NativeLinkTransport.EVENT_ERROR),
                ("YL_EVENT_LOG", NativeLinkTransport.EVENT_LOG),
                ("YL_LOG_ERROR", (long) NativeLogLevel.Error),
                ("YL_LOG_WARN", (long) NativeLogLevel.Warning),
                ("YL_LOG_INFO", (long) NativeLogLevel.Info),
                ("YL_LOG_DEBUG", (long) NativeLogLevel.Debug),
                ("YL_LOG_TRACE", (long) NativeLogLevel.Trace),
                ("YL_PATH_DIRECT", NativeLinkTransport.PATH_DIRECT),
                ("YL_PATH_RELAY", NativeLinkTransport.PATH_RELAY),
            };

            Assert.That(header, Is.Not.Empty, "found no constants in yarg_link.h");
            foreach (var (name, value) in copies)
            {
                Assert.That(header.ContainsKey(name), $"yarg_link.h has no {name}");
                Assert.That(value, Is.EqualTo(header[name]), $"the C# copy of {name}");
            }
        }
    }
}
