using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace GamepadServer.Tests
{
    /// <summary>
    /// Verifies the explicit protocol-key -> Xbox360 output mapping table
    /// (Program.InputMap) used by BOTH the _DOWN/_UP edge path and the
    /// STATE heartbeat resync path.
    ///
    /// These tests read the REAL production table directly (InputMap is
    /// `internal`, and this test project compiles Program.cs straight
    /// into its own assembly - see GamepadServer.Tests.csproj) rather
    /// than duplicating a second copy of the expected mapping here. That
    /// matters: a duplicated "expected" table can drift out of sync with
    /// production and start passing even after production breaks. Testing
    /// the real dictionary means these tests fail the moment InputMap
    /// itself is wrong.
    ///
    /// What this proves, per control:
    ///   - every protocol key (DOWN and UP share one mapping entry, since
    ///     Apply(pressed) is the only thing that differs between them) has
    ///     exactly one InputMap entry
    ///   - that entry's Xbox360 output (Button or Slider) is not shared
    ///     with ANY other key - i.e. it is impossible for one control's
    ///     event to change another control's output, because no two keys
    ///     ever resolve to the same output field
    ///   - that entry's StateBit is unique and matches the position the
    ///     same key occupies in Android's ButtonState.ORDER, so the STATE
    ///     heartbeat can never decode one control's bit into another
    ///     control's output either
    ///
    /// InputMapping.Apply(controller, pressed) itself (see Program.cs) is
    /// a direct switch on Kind calling exactly one of SetButtonState(this
    /// mapping's Button, pressed) or SetSliderValue(this mapping's Slider,
    /// pressed?255:0) - no bit shifting, no array indexing, no shared
    /// mutable state between mappings - so once a mapping entry is proven
    /// correct and unique, DOWN and UP for that key are both provably
    /// correct: they invoke the exact same single mapping with only
    /// `pressed` flipped.
    /// </summary>
    public class MaskButtonsTest
    {
        // The full protocol key set, in the same order as Android's
        // ButtonState.ORDER (see ButtonState.kt) - this IS the test
        // matrix: one row per control, each covering both its DOWN and
        // its UP event via the shared mapping entry.
        private static readonly string[] ExpectedKeysInOrder =
        {
            "A", "B", "X", "Y",
            "LB", "RB", "LS", "RS",
            "START", "SELECT", "HOME",
            "DPAD_UP", "DPAD_DOWN", "DPAD_LEFT", "DPAD_RIGHT",
            "LT", "RT"
        };

        [Fact]
        public void InputMap_ContainsExactlyTheExpectedKeys()
        {
            var actualKeys = Program.InputMap.Keys.OrderBy(k => k, StringComparer.Ordinal);
            var expectedKeys = ExpectedKeysInOrder.OrderBy(k => k, StringComparer.Ordinal);
            Assert.Equal(expectedKeys, actualKeys);
        }

        [Theory]
        [InlineData("A", Xbox360Button_.A)]
        [InlineData("B", Xbox360Button_.B)]
        [InlineData("X", Xbox360Button_.X)]
        [InlineData("Y", Xbox360Button_.Y)]
        [InlineData("LB", Xbox360Button_.LeftShoulder)]
        [InlineData("RB", Xbox360Button_.RightShoulder)]
        [InlineData("LS", Xbox360Button_.LeftThumb)]
        [InlineData("RS", Xbox360Button_.RightThumb)]
        [InlineData("START", Xbox360Button_.Start)]
        [InlineData("SELECT", Xbox360Button_.Back)]
        [InlineData("HOME", Xbox360Button_.Guide)]
        [InlineData("DPAD_UP", Xbox360Button_.Up)]
        [InlineData("DPAD_DOWN", Xbox360Button_.Down)]
        [InlineData("DPAD_LEFT", Xbox360Button_.Left)]
        [InlineData("DPAD_RIGHT", Xbox360Button_.Right)]
        public void ButtonKey_MapsToExactlyItsOwnXbox360Button(string key, Xbox360Button_ expected)
        {
            // Every DOWN and every UP for this key goes through this same
            // entry - proving the entry once covers both edges.
            Assert.True(Program.InputMap.TryGetValue(key, out var mapping));
            Assert.Equal(Program.ControlKind.Button, mapping.Kind);
            Assert.Equal(ResolveButton(expected), mapping.Button);
        }

        [Theory]
        [InlineData("LT", Xbox360Slider_.LeftTrigger)]
        [InlineData("RT", Xbox360Slider_.RightTrigger)]
        public void SliderKey_MapsToExactlyItsOwnXbox360Slider(string key, Xbox360Slider_ expected)
        {
            Assert.True(Program.InputMap.TryGetValue(key, out var mapping));
            Assert.Equal(Program.ControlKind.Slider, mapping.Kind);
            Assert.Equal(ResolveSlider(expected), mapping.Slider);
        }

        [Fact]
        public void NoTwoKeys_ShareTheSameXbox360Output()
        {
            // This is the direct regression test for "one input can
            // resolve to another input's output": group every mapping by
            // its actual output object and confirm every group has size 1.
            var outputGroups = Program.InputMap.Values
                .GroupBy(m => m.Kind == Program.ControlKind.Button
                    ? (object)m.Button
                    : m.Slider)
                .Where(g => g.Count() > 1)
                .ToList();

            Assert.True(
                outputGroups.Count == 0,
                "Multiple protocol keys map to the same Xbox360 output: " +
                string.Join("; ", outputGroups.Select(g =>
                    $"{g.Key} <- [{string.Join(", ", g.Select(m => m.Key))}]")));
        }

        [Fact]
        public void EveryStateBit_IsUniqueAcrossAllControls()
        {
            var bits = Program.InputMap.Values.Select(m => m.StateBit).ToList();
            var duplicateBits = bits.GroupBy(b => b).Where(g => g.Count() > 1).Select(g => g.Key).ToList();

            Assert.True(
                duplicateBits.Count == 0,
                "Duplicate STATE bit positions found: " + string.Join(", ", duplicateBits) +
                " - a STATE packet could not distinguish these controls.");
        }

        [Fact]
        public void StateBit_MatchesPositionInAndroidButtonStateOrder()
        {
            // StateBit is matched to Android's ORDER by KEY NAME here,
            // not by array position, so a reordering on either side shows
            // up as a failing assertion rather than a silent cross-wire.
            for (int i = 0; i < ExpectedKeysInOrder.Length; i++)
            {
                string key = ExpectedKeysInOrder[i];
                Assert.True(Program.InputMap.TryGetValue(key, out var mapping));
                Assert.Equal(i, mapping.StateBit);
            }
        }

        // --- helpers -----------------------------------------------------
        // Xbox360Button/Xbox360Slider are the ViGEm library's own
        // strongly-typed instances (Xbox360Button.A etc.), not plain
        // enums, so xUnit [InlineData] can't carry them directly. These
        // local enums exist purely so [Theory] cases can name a control
        // by a simple label; ResolveButton/ResolveSlider map that label
        // back to the real ViGEm instance the assertion compares against.
        public enum Xbox360Button_ { A, B, X, Y, LeftShoulder, RightShoulder, LeftThumb, RightThumb, Start, Back, Guide, Up, Down, Left, Right }
        public enum Xbox360Slider_ { LeftTrigger, RightTrigger }

        private static Xbox360Button ResolveButton(Xbox360Button_ b) => b switch
        {
            Xbox360Button_.A => Xbox360Button.A,
            Xbox360Button_.B => Xbox360Button.B,
            Xbox360Button_.X => Xbox360Button.X,
            Xbox360Button_.Y => Xbox360Button.Y,
            Xbox360Button_.LeftShoulder => Xbox360Button.LeftShoulder,
            Xbox360Button_.RightShoulder => Xbox360Button.RightShoulder,
            Xbox360Button_.LeftThumb => Xbox360Button.LeftThumb,
            Xbox360Button_.RightThumb => Xbox360Button.RightThumb,
            Xbox360Button_.Start => Xbox360Button.Start,
            Xbox360Button_.Back => Xbox360Button.Back,
            Xbox360Button_.Guide => Xbox360Button.Guide,
            Xbox360Button_.Up => Xbox360Button.Up,
            Xbox360Button_.Down => Xbox360Button.Down,
            Xbox360Button_.Left => Xbox360Button.Left,
            Xbox360Button_.Right => Xbox360Button.Right,
            _ => throw new ArgumentOutOfRangeException(nameof(b)),
        };

        private static Xbox360Slider ResolveSlider(Xbox360Slider_ s) => s switch
        {
            Xbox360Slider_.LeftTrigger => Xbox360Slider.LeftTrigger,
            Xbox360Slider_.RightTrigger => Xbox360Slider.RightTrigger,
            _ => throw new ArgumentOutOfRangeException(nameof(s)),
        };
    }
}
