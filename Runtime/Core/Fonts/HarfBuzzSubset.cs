using System;
using OneText.Native;

namespace OneText
{
    /// <summary>
    /// Whether the HarfBuzz we loaded can subset fonts.
    ///
    /// `harfbuzz-subset` is a second library in HarfBuzz's build, and a binary
    /// can be a perfectly good shaper without it. That makes subsetting a
    /// feature that either exists or does not depending on which platform's
    /// binary got loaded, the worst kind of feature. Every binary this package
    /// vendors is checked at vendor time (see Docs/NATIVES.md) and again here at
    /// runtime, so a future re-vendor that quietly drops it is caught by a test
    /// rather than by a user whose build got 40 MB bigger.
    /// </summary>
    public static class HarfBuzzSubset
    {
        private static bool? s_available;

        /// <summary>True when the loaded HarfBuzz exports the subsetting API.</summary>
        public static bool IsAvailable
        {
            get
            {
                if (s_available.HasValue) return s_available.Value;
                try
                {
                    // Creating and destroying an empty input is the cheapest
                    // question that has to reach the library to be answered.
                    var input = HarfBuzzApi.hb_subset_input_create_or_fail();
                    if (input != IntPtr.Zero) HarfBuzzApi.hb_subset_input_destroy(input);
                    s_available = input != IntPtr.Zero;
                }
                catch (EntryPointNotFoundException)
                {
                    // A shaper-only build: the symbol is not there at all.
                    s_available = false;
                }
                catch (DllNotFoundException)
                {
                    s_available = false;
                }
                return s_available.Value;
            }
        }
    }
}
