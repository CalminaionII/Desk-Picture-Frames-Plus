using Xunit;
using DeskPictureFrame;

namespace DeskPictureFrame.Tests
{
    public class ParseBoxCollectionTests
    {
        [Fact]
        public void NullPath_ReturnsNull()
        {
            Assert.Null(DeskPictureFrameValidation.ParseBoxCollection(null));
        }

        [Fact]
        public void EmptyPath_ReturnsNull()
        {
            Assert.Null(DeskPictureFrameValidation.ParseBoxCollection(""));
        }

        [Fact]
        public void NonBoxPath_ReturnsNull()
        {
            Assert.Null(DeskPictureFrameValidation.ParseBoxCollection("desklandscapesingle-1"));
        }

        [Fact]
        public void PortraitSingleBox_ReturnsPortraitSingle()
        {
            Assert.Equal("portraitsingle", DeskPictureFrameValidation.ParseBoxCollection("deskframebox-portraitsingle"));
        }

        [Fact]
        public void LandscapeSingleBox_ReturnsLandscapeSingle()
        {
            Assert.Equal("landscapesingle", DeskPictureFrameValidation.ParseBoxCollection("deskframebox-landscapesingle"));
        }

        [Fact]
        public void PortraitWallBox_ReturnsPortraitWall()
        {
            Assert.Equal("portraitwall", DeskPictureFrameValidation.ParseBoxCollection("deskframebox-portraitwall"));
        }

        [Fact]
        public void LandscapeWallBox_ReturnsLandscapeWall()
        {
            Assert.Equal("landscapewall", DeskPictureFrameValidation.ParseBoxCollection("deskframebox-landscapewall"));
        }

        [Fact]
        public void PrefixOnlyReturnsEmptyString()
        {
            Assert.Equal("", DeskPictureFrameValidation.ParseBoxCollection("deskframebox-"));
        }

        [Fact]
        public void UnknownCollectionStillParsed()
        {
            Assert.Equal("unknowncollection", DeskPictureFrameValidation.ParseBoxCollection("deskframebox-unknowncollection"));
        }
    }

    public class GetFrameAssetCodeTests
    {
        [Theory]
        [InlineData("portraitsingle", 1, "deskpictureframe:deskportraitsingle-1")]
        [InlineData("portraitsingle", 2, "deskpictureframe:deskportraitsingle-2")]
        [InlineData("portraitsingle", 3, "deskpictureframe:deskportraitsingle-3")]
        [InlineData("portraitsingle", 4, "deskpictureframe:deskportraitsingle-4")]
        [InlineData("portraitsingle", 5, "deskpictureframe:deskportraitsingle-5")]
        public void PortraitSingle_GeneratesCorrectCode(string collection, int index, string expected)
        {
            Assert.Equal(expected, DeskPictureFrameValidation.GetFrameAssetCode(collection, index));
        }

        [Theory]
        [InlineData("landscapesingle", 1, "deskpictureframe:desklandscapesingle-1")]
        [InlineData("landscapesingle", 2, "deskpictureframe:desklandscapesingle-2")]
        [InlineData("landscapesingle", 3, "deskpictureframe:desklandscapesingle-3")]
        [InlineData("landscapesingle", 4, "deskpictureframe:desklandscapesingle-4")]
        [InlineData("landscapesingle", 5, "deskpictureframe:desklandscapesingle-5")]
        public void LandscapeSingle_GeneratesCorrectCode(string collection, int index, string expected)
        {
            Assert.Equal(expected, DeskPictureFrameValidation.GetFrameAssetCode(collection, index));
        }

        [Theory]
        [InlineData("portraitwall", 1, "deskpictureframe:wall-portrait-1-north")]
        [InlineData("portraitwall", 2, "deskpictureframe:wall-portrait-2-north")]
        [InlineData("portraitwall", 3, "deskpictureframe:wall-portrait-3-north")]
        [InlineData("portraitwall", 4, "deskpictureframe:wall-portrait-4-north")]
        [InlineData("portraitwall", 5, "deskpictureframe:wall-portrait-5-north")]
        public void PortraitWall_GeneratesCorrectCode(string collection, int index, string expected)
        {
            Assert.Equal(expected, DeskPictureFrameValidation.GetFrameAssetCode(collection, index));
        }

        [Theory]
        [InlineData("landscapewall", 1, "deskpictureframe:wall-landscape-1-north")]
        [InlineData("landscapewall", 2, "deskpictureframe:wall-landscape-2-north")]
        [InlineData("landscapewall", 3, "deskpictureframe:wall-landscape-3-north")]
        [InlineData("landscapewall", 4, "deskpictureframe:wall-landscape-4-north")]
        [InlineData("landscapewall", 5, "deskpictureframe:wall-landscape-5-north")]
        public void LandscapeWall_GeneratesCorrectCode(string collection, int index, string expected)
        {
            Assert.Equal(expected, DeskPictureFrameValidation.GetFrameAssetCode(collection, index));
        }

        [Theory]
        [InlineData("unknowncollection", 1)]
        [InlineData("", 1)]
        [InlineData("somethingelse", 3)]
        public void UnknownCollection_ReturnsNull(string collection, int index)
        {
            Assert.Null(DeskPictureFrameValidation.GetFrameAssetCode(collection, index));
        }

        [Fact]
        public void NullCollection_ReturnsNull()
        {
            Assert.Null(DeskPictureFrameValidation.GetFrameAssetCode(null, 1));
        }
    }
}
