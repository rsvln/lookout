using Xunit;

namespace Lookout.Tests
{
    public class ClipChunkTests
    {
        static List<DbRow> Segments(params long[] sizes) => sizes.Select((s, i) => new DbRow { path = "s" + i, size = s }).ToList();

        static string[] Names(List<DbRow> chunk) => chunk.Select(r => r.path).ToArray();

        [Fact]
        public void SmallClip_StaysInOnePiece()
        {
            var chunks = Program.SplitClipChunks(Segments(10, 20, 30), check: 100, split: 40);
            Assert.Single(chunks);
            Assert.Equal(new[] { "s0", "s1", "s2" }, Names(chunks[0]));
        }

        [Fact]
        public void ClipExactlyAtTheLimit_IsNotSplit()
        {
            Assert.Single(Program.SplitClipChunks(Segments(50, 50), check: 100, split: 10));
        }

        [Fact]
        public void BigClip_IsSplitIntoConsecutiveGroups()
        {
            var chunks = Program.SplitClipChunks(Segments(30, 30, 30, 30, 30), check: 100, split: 70);
            Assert.Equal(3, chunks.Count);
            Assert.Equal(new[] { "s0", "s1" }, Names(chunks[0]));
            Assert.Equal(new[] { "s2", "s3" }, Names(chunks[1]));
            Assert.Equal(new[] { "s4" }, Names(chunks[2]));
        }

        [Fact]
        public void SegmentBiggerThanSplitSize_GetsItsOwnPiece()
        {
            var chunks = Program.SplitClipChunks(Segments(10, 500, 10), check: 100, split: 60);
            Assert.Equal(3, chunks.Count);
            Assert.Equal(new[] { "s1" }, Names(chunks[1]));
        }

        [Fact]
        public void Split_KeepsEverySegmentExactlyOnceInOrder()
        {
            var all = Segments(5, 17, 40, 3, 90, 12, 8, 55, 1, 33);
            var chunks = Program.SplitClipChunks(all, check: 50, split: 60);
            Assert.Equal(all.Select(r => r.path), chunks.SelectMany(c => c).Select(r => r.path));
        }
    }
}
