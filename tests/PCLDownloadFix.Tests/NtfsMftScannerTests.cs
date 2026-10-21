using Xunit;

namespace PCLDownloadFix.Tests
{
    public class NtfsMftScannerTests
    {
        [Fact]
        public void RootFileReference_WithSequenceNumber_MapsToRecordFive()
        {
            // fsutil file queryfileid E:\ reports 0x0005000000000005.
            Assert.Equal(5UL, NtfsMftScanner.MaskFileReference(0x0005000000000005));
        }

        [Fact]
        public void NormalRecordReference_KeepsLow48Bits()
        {
            Assert.Equal(0x25BEFAUL, NtfsMftScanner.MaskFileReference(0x000100000025BEFA));
        }
    }
}
