using ProtoBuf;
using System.IO;
using Xunit;

namespace Examples.Issues
{
    public class SO66453263 // handling zero-padded data
    {

        static MemoryStream GetZeroPaddedData()
        {
            var ms = new MemoryStream();
            Serializer.Serialize(ms, new Foo { Id = 42, Name = "abc" });
            for (int i = 0; i < 10; i++) ms.WriteByte(0); // add some zero padding
            ms.Position = 0;
            return ms;
        }

        [Fact]
        public void ZeroPaddingFailsByDefault()
        {
            using var ms = GetZeroPaddedData();
            var ex = Assert.Throws<ProtoException>(() => Serializer.Deserialize<Foo>(ms));
            Assert.Equal("Invalid field in source data: 0", ex.Message);
        }

        [Fact]
        public void ZeroPaddingWorksWithCustomStateContext()
        {
            using var ms = GetZeroPaddedData();
            var obj = Serializer.Deserialize<Foo>(ms, userState: MyCustomState.Instance);
            Assert.Equal(42, obj.Id);
            Assert.Equal("abc", obj.Name);
        }

        class MyCustomState : ISerializationOptions
        {
            public static MyCustomState Instance { get; } = new MyCustomState();

            SerializationOptions ISerializationOptions.Options => SerializationOptions.AllowZeroPadding;

            private MyCustomState() { }
        }

        [ProtoContract]
        public class Foo
        {
            [ProtoMember(1)]
            public int Id { get; set; }

            [ProtoMember(2)]
            public string Name { get; set; }
        }
    }
}
