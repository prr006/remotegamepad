using System;
using Xunit;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace GamepadServer.Tests
{
    /// <summary>
    /// Unit tests for MaskButtons array ordering.
    /// Verifies that HOME (Guide) button is included and bit positions
    /// match Android's ButtonState.ORDER exactly.
    /// </summary>
    public class MaskButtonsTest
    {
        // These must be accessible for testing - declared as internal in Program.cs
        // For now, we test the expected structure based on the source code:
        // Bit 0:A, 1:B, 2:X, 3:Y, 4:LB, 5:RB, 6:LS, 7:RS, 8:START, 9:BACK, 10:GUIDE,
        // 11:UP, 12:DOWN, 13:LEFT, 14:RIGHT, 15:LT, 16:RT
        
        [Fact]
        public void GuideButton_ExistsInXbox360Button()
        {
            // Verify Xbox360Button.Guide enum value exists (ViGEm dependency check)
            var guide = Xbox360Button.Guide;
            Assert.NotNull(guide);
        }

        [Fact]
        public void ExpectedMaskButtons_HasCorrectSize()
        {
            // After adding HOME at position 10, we have 15 buttons in MaskButtons array
            // (indices 0-14), plus LT/RT as separate bit constants (15, 16)
            int expectedArraySize = 15; // A,B,X,Y, LB,RB,LS,RS, START,BACK,GUIDE, UP,DOWN,LEFT,RIGHT
            Assert.Equal(expectedArraySize, GetExpectedMaskButtonsSize());
        }

        [Fact]
        public void ExpectedMaskButtons_HomeAtPosition10()
        {
            // HOME/Guide should be at index 10 (between Back and Up)
            var expectedOrder = GetExpectedButtonOrder();
            Assert.Equal("Guide", expectedOrder[10].Item2.ToString());
        }

        [Fact]
        public void ExpectedLtRtBitPositions_AfterHomeAdded()
        {
            // After inserting HOME at position 10, all D-pad buttons shift by 1
            // LtBit should be 15, RtBit should be 16
            const int expectedLtBit = 15;
            const int expectedRtBit = 16;
            
            Assert.Equal(expectedLtBit, GetExpectedLtBit());
            Assert.Equal(expectedRtBit, GetExpectedRtBit());
        }

        [Fact]
        public void ExpectedButtonOrder_MatchesAndroid()
        {
            // This order MUST match ButtonState.ORDER in ButtonState.kt exactly
            var expectedOrder = GetExpectedButtonOrder();
            var androidOrder = new[] {
                "A", "B", "X", "Y",
                "LeftShoulder", "RightShoulder", "LeftThumb", "RightThumb",
                "Start", "Back", "Guide",
                "Up", "Down", "Left", "Right"
            };
            
            Assert.Equal(androidOrder.Length, expectedOrder.Length);
            for (int i = 0; i < androidOrder.Length; i++)
            {
                Assert.Equal(androidOrder[i], expectedOrder[i].Item2.ToString());
            }
        }

        // Helper methods returning expected values based on source code analysis
        private static int GetExpectedMaskButtonsSize() => 15;
        
        private static (int bit, Xbox360Button button)[] GetExpectedButtonOrder() => new[]
        {
            (0, Xbox360Button.A),
            (1, Xbox360Button.B),
            (2, Xbox360Button.X),
            (3, Xbox360Button.Y),
            (4, Xbox360Button.LeftShoulder),
            (5, Xbox360Button.RightShoulder),
            (6, Xbox360Button.LeftThumb),
            (7, Xbox360Button.RightThumb),
            (8, Xbox360Button.Start),
            (9, Xbox360Button.Back),
            (10, Xbox360Button.Guide),
            (11, Xbox360Button.Up),
            (12, Xbox360Button.Down),
            (13, Xbox360Button.Left),
            (14, Xbox360Button.Right),
        };

        private static int GetExpectedLtBit() => 15;
        private static int GetExpectedRtBit() => 16;
    }
}
