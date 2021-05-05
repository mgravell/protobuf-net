using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Google.Protobuf.Reflection;
using ProtoBuf.Reflection;
using Xunit;

namespace ProtoBuf.Issues
{
    public class Issue740
    {
        [Fact]
        public void MessagesInServicesHaveFullNamespaceIfExternal()
        {
            const string mainProtoText = @"syntax = ""proto3"";
package MainPackage;

import ""child_proto_text.proto"";

message ParentRequest { }
message ParentReply { }

service MyService {
    rpc MyMethodExternalMessage(ChildPackage.ChildRequest) returns (ChildPackage.ChildReply);
    rpc MyMethodInternalMessage(ParentRequest) returns (ParentReply);
}
";

            const string childProtoText = @"syntax = ""proto3"";
package ChildPackage;

message ChildRequest { }
message ChildReply { }
";

            var set = new FileDescriptorSet();
            set.Add("main_proto_text.proto", source: new StringReader(mainProtoText));
            set.Add("child_proto_text.proto", source: new StringReader(childProtoText));
            set.Process();
            var errors = set.GetErrors();
            Assert.Empty(errors);

            var generateOptions = new Dictionary<string, string> { ["services"] = "true" };

            var sourceFiles = CSharpCodeGenerator
                .Default
                .Generate(set, options: generateOptions)
                .Select(x => x.Text)
                .ToArray();

            // Find the request/reply types from the service interface
            var mainProtoSource = sourceFiles[0];

            var regex = new Regex(@"$\s+global::System\.Threading\.Tasks\.ValueTask<(?<Reply>\S+)> MyMethodExternalMessageAsync\((?<Request>\S+) value,", RegexOptions.Multiline);
            var match = regex.Match(mainProtoSource);

            Assert.Equal("global::ChildPackage.ChildReply", match.Groups["Reply"].Value);
            Assert.Equal("global::ChildPackage.ChildRequest", match.Groups["Request"].Value);

            regex = new Regex(@"$\s+global::System\.Threading\.Tasks\.ValueTask<(?<Reply>\S+)> MyMethodInternalMessageAsync\((?<Request>\S+) value,", RegexOptions.Multiline);
            match = regex.Match(mainProtoSource);

            Assert.Equal("ParentReply", match.Groups["Reply"].Value);
            Assert.Equal("ParentRequest", match.Groups["Request"].Value);
        }
    }
}
