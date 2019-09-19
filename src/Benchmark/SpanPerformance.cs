﻿using BenchmarkDotNet.Attributes;
using ProtoBuf;
using ProtoBuf.Meta;
using System;
using System.Buffers;
using System.IO;

#if NEW_API
namespace Benchmark
{
    [ClrJob, CoreJob, MemoryDiagnoser]
    public class SpanPerformance
    {
        private MemoryStream _ms;
        private ReadOnlySequence<byte> _ros;
        public ProtoReader ReadMS(out ProtoReader.State state)
            => ProtoReader.Create(out state, _ms, Model);

        public ProtoReader ReadROS(out ProtoReader.State state)
            => ProtoReader.Create(out state, _ros, Model);

        public ProtoReader ReadROM(out ProtoReader.State state)
        {
            if (!_ros.IsSingleSegment) throw new InvalidOperationException("Expected single segment");
            return ProtoReader.Create(out state, _ros.First, Model);
        }

        public TypeModel Model => RuntimeTypeModel.Default;

        [GlobalSetup]
        public void Setup()
        {
            var data = File.ReadAllBytes("nwind.proto.bin");
            _ms = new MemoryStream(data);
            _ros = new ReadOnlySequence<byte>(data);
        }

        [Benchmark(Baseline = true)]
        public void MemoryStream()
        {
            _ms.Position = 0;
            using var reader = ReadMS(out var state);
            var dal = reader.Deserialize<protogen.Database>(ref state);
            GC.KeepAlive(dal);
        }

        [Benchmark]
        public void ReadOnlySequence()
        {
            using var reader = ReadROS(out var state);
            var dal = reader.Deserialize<protogen.Database>(ref state);
            GC.KeepAlive(dal);
        }
        [Benchmark]
        public void ReadOnlyMemory()
        {
            using var reader = ReadROM(out var state);
            var dal = reader.Deserialize<protogen.Database>(ref state);
            GC.KeepAlive(dal);
        }
    }
}
#endif