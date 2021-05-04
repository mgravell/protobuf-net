using ProtoBuf.Meta;
using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace ProtoBuf.Issues
{
    [ProtoContract]
    public class TestContract
    {
        [ProtoMember(1)]
        public DateTime DateTime { get; set; }

        [ProtoMember(2)]
        public string Test { get; set; }
    }

    public class Issue748
    {
        [Theory]
        [MemberData(nameof(DateTimes))]
        public void ShouldIncludeDateTimeKindForDateTime(DateTime dateTime, DateTimeKind dateTimeKind)
        {
            var typeModel = CreateTypeModel();
            var sourceObject = new TestContract
            {
                DateTime = DateTime.SpecifyKind(dateTime, dateTimeKind),
                Test = "abc"
            };

            using var sourceStream = new MemoryStream();
            typeModel.Serialize(sourceStream, sourceObject);
            var destinationStream = new MemoryStream(sourceStream.ToArray());
            var deserializedObject = typeModel.Deserialize<TestContract>(destinationStream);

            Assert.Equal(dateTime, deserializedObject.DateTime);
            Assert.Equal(dateTimeKind, deserializedObject.DateTime.Kind);
        }

        public static IEnumerable<object[]> DateTimes =>
            new List<object[]>
            {
                new object[] { DateTime.MinValue, DateTimeKind.Utc },
                new object[] { DateTime.MinValue, DateTimeKind.Local },
                new object[] { DateTime.MinValue, DateTimeKind.Unspecified },
                new object[] { new DateTime(2020, 12, 25), DateTimeKind.Utc },
                new object[] { new DateTime(2020, 12, 25), DateTimeKind.Local },
                new object[] { new DateTime(2020, 12, 25), DateTimeKind.Unspecified },
                new object[] { DateTime.MaxValue, DateTimeKind.Utc },
                new object[] { DateTime.MaxValue, DateTimeKind.Local },
                new object[] { DateTime.MaxValue, DateTimeKind.Unspecified }
            };

        public static TypeModel CreateTypeModel()
        {
            var typeModel = RuntimeTypeModel.Create();
            typeModel.IncludeDateTimeKind = true;
            return typeModel;
        }
    }
}
