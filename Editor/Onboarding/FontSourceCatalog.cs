using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace OneText.Editor
{
    /// <summary>What the catalogue is willing to say about a font it was asked about.</summary>
    public enum FontSourceMatch
    {
        /// <summary>Nothing. The caller is on their own, and is told so.</summary>
        None,

        /// <summary>Identified, redistributable, and there is a file to fetch.</summary>
        Download,

        /// <summary>
        /// Identified, and the licence is known, but the publisher does not put
        /// the file anywhere a program may reasonably take it from. The user is
        /// pointed at the place a person can.
        /// </summary>
        Manual,
    }

    /// <summary>
    /// One answer from the catalogue: which font this is, where its file comes
    /// from, and under what licence.
    ///
    /// The licence fields are filled for every match, and they are filled
    /// <em>before</em> anything is downloaded, because the point of showing them
    /// is that the person clicking the button is agreeing to something they have
    /// read the name of.
    /// </summary>
    public struct FontSourceCandidate
    {
        public FontSourceMatch Match;

        /// <summary>The family as the catalogue spells it: "Josefin Sans".</summary>
        public string FamilyName;

        /// <summary>The name the file is saved under, e.g. <c>Cairo.ttf</c>.</summary>
        public string FileName;

        /// <summary>Where the font file is fetched from, or null for a manual match.</summary>
        public string DownloadUrl;

        /// <summary>Human name of the licence: "SIL Open Font License 1.1".</summary>
        public string LicenceName;

        /// <summary>A page a person can read the licence on.</summary>
        public string LicenceUrl;

        /// <summary>
        /// The licence file that ships with the font, saved next to it when the
        /// source has one. Null when the source publishes no such file.
        /// </summary>
        public string LicenceFileUrl;

        /// <summary>
        /// Where a person goes to get this font themselves. Always worth having
        /// — it is the whole answer for a manual match and the fallback for a
        /// download that fails.
        /// </summary>
        public string HomeUrl;

        /// <summary>
        /// The one thing about this font that would otherwise surprise someone:
        /// usually that the file is a variable font and the weight they had is
        /// now an axis. Shown next to the licence, before the download.
        /// </summary>
        public string Note;

        /// <summary>
        /// True when this came from looking the family up rather than from the
        /// hand-written list. Worth showing: a hand-written entry has had a
        /// person read the licence and the file, and a resolved one has had a
        /// repository consulted, and those are different amounts of confidence.
        /// </summary>
        public bool Resolved;

        public bool Found => Match != FontSourceMatch.None;
    }

    /// <summary>
    /// What one attempt to look a family up in the Google Fonts repository did.
    ///
    /// A separate type from <see cref="FontSourceCandidate"/> because failing to
    /// find a font is not the same as finding that there is no font: an offline
    /// machine and a family that genuinely is not published both end with no
    /// candidate, and only one of them is worth trying again.
    /// </summary>
    public struct FontSourceResolution
    {
        public FontSourceOutcome Outcome;

        /// <summary>What was found, when something was. Meaningless otherwise.</summary>
        public FontSourceCandidate Candidate;

        /// <summary>A sentence for the user, whether this worked or not.</summary>
        public string Message;

        public bool Ok => Outcome == FontSourceOutcome.Ok;
    }

    /// <summary>How a download ended. Every one of these is a sentence, not an exception.</summary>
    public enum FontSourceOutcome
    {
        Ok,

        /// <summary>Nothing was tried: the catalogue does not know this font.</summary>
        NoCandidate,

        /// <summary>The name resolved but nothing answered: no network, or DNS.</summary>
        Offline,

        /// <summary>The server answered, and what it said was no.</summary>
        NotFound,

        Timeout,

        /// <summary>Somebody cancelled the progress bar.</summary>
        Cancelled,

        /// <summary>Bytes arrived and they are not a font. Nothing was written.</summary>
        NotAFont,

        /// <summary>The font was fine; writing it into the project was not.</summary>
        WriteFailed,
    }

    /// <summary>What one download did, and where the file went.</summary>
    public struct FontSourceDownload
    {
        public FontSourceOutcome Outcome;

        /// <summary>Project path of the font file, when one was written.</summary>
        public string FontPath;

        /// <summary>Project path of the licence file, when the source had one.</summary>
        public string LicencePath;

        /// <summary>A sentence for the user. Filled whether this worked or not.</summary>
        public string Message;

        public bool Ok => Outcome == FontSourceOutcome.Ok;
    }

    /// <summary>
    /// A short, hand-written list of fonts that may legally be fetched, and the
    /// code to fetch one.
    ///
    /// This exists because the common ending of a TextMesh Pro migration is a
    /// project holding twenty baked atlases and four font files, and most of the
    /// missing twenty turn out to be open-licensed fonts that anyone may
    /// download — the pack that shipped them simply left the <c>.ttf</c> out.
    /// Telling somebody "find these eighteen fonts yourself" when fourteen of
    /// them are one HTTP request away is not caution, it is unhelpfulness.
    ///
    /// It is a curated list and not a search, on purpose. A search that matches
    /// "Cairo" to "Cairo Play", or "Noto Sans" to "Noto Sans KR", installs a
    /// typeface that is not the one the project had, and the project keeps
    /// rendering — differently, everywhere, with nothing to notice. So matching
    /// is exact on a normalised family name and nothing else: a near miss is
    /// reported as a miss, and the person is told to find that one themselves.
    ///
    /// Eleven families is not an answer to a world with tens of thousands of
    /// fonts in it, though, and <see cref="Resolve"/> is the way past that: the
    /// same exactness, applied to a directory name in the Google Fonts
    /// repository, which covers roughly 1,800 openly licensed families. The
    /// hand-written entries stay and win, because they carry things a lookup
    /// cannot produce — a sentence somebody wrote about what will surprise you,
    /// and sources that are not Google's at all.
    ///
    /// Nothing here happens on its own. There is no fetch during a scan, no
    /// bulk download, and no request at all until somebody presses a button for
    /// one named font whose licence they have just been shown. That holds for
    /// the lookup as much as for the download: <see cref="Match"/> is offline,
    /// synchronous and free, and is the only one of the two a scan may call.
    /// </summary>
    public static class FontSourceCatalog
    {
        /// <summary>Seconds a single request is given before it is called a timeout.</summary>
        public const int TimeoutSeconds = 30;

        private const string Ofl = "SIL Open Font License 1.1";
        private const string OflUrl = "https://openfontlicense.org/";
        private const string GoogleRepo = "https://raw.githubusercontent.com/google/fonts/main/";
        private const string GoogleFonts = GoogleRepo + "ofl/";

        /// <summary>
        /// The list. Keys are normalised family names — see <see cref="Key"/> —
        /// and the value is what the caller is shown before it decides.
        ///
        /// Google Fonts is served from the project's own repository rather than
        /// through the web API: the API hands back CSS pointing at WOFF2, and
        /// OneText rasterises from an sfnt. Most of these families are variable
        /// now, which is why so many of the notes say the same thing — one file
        /// arrives where a project used to have four, and the weights are axes
        /// on it rather than separate faces.
        /// </summary>
        private static readonly Dictionary<string, FontSourceCandidate> Entries =
            new Dictionary<string, FontSourceCandidate>
            {
                ["cairo"] = Google("Cairo", "cairo", "Cairo.ttf", "Cairo%5Bslnt%2Cwght%5D.ttf",
                    "This is the variable Cairo: one file covering weights 200–1000 plus a slant " +
                    "axis. A label that used Cairo-Bold wants wght 700 on it."),

                ["sen"] = Google("Sen", "sen", "Sen.ttf", "Sen%5Bwght%5D.ttf",
                    "Variable: weights 400–800 on one file, as axes rather than faces."),

                ["alata"] = Google("Alata", "alata", "Alata-Regular.ttf", "Alata-Regular.ttf",
                    "Alata ships one weight and no axes, so this is the whole family."),

                ["josefinsans"] = Google("Josefin Sans", "josefinsans", "JosefinSans.ttf",
                    "JosefinSans%5Bwght%5D.ttf",
                    "Variable: weights 100–700 as an axis. The italics are a separate file this " +
                    "catalogue does not fetch."),

                ["tiltwarp"] = Google("Tilt Warp", "tiltwarp", "TiltWarp.ttf",
                    "TiltWarp%5BXROT%2CYROT%5D.ttf",
                    "A display face with two rotation axes and one weight."),

                ["notosans"] = Google("Noto Sans", "notosans", "NotoSans.ttf",
                    "NotoSans%5Bwdth%2Cwght%5D.ttf",
                    "Variable Latin/Greek/Cyrillic Noto Sans. It does not cover CJK: those are " +
                    "separate families, and Noto Sans KR is one of them."),

                ["notosanskr"] = Google("Noto Sans KR", "notosanskr", "NotoSansKR.ttf",
                    "NotoSansKR%5Bwght%5D.ttf",
                    "Korean Noto, variable by weight. It is a 5–6 MB download and a large font " +
                    "asset; subset it if the build size matters."),

                ["pretendard"] = new FontSourceCandidate
                {
                    Match = FontSourceMatch.Download,
                    FamilyName = "Pretendard",
                    FileName = "PretendardVariable.ttf",
                    DownloadUrl = "https://raw.githubusercontent.com/orioncactus/pretendard/main/" +
                                  "packages/pretendard/dist/public/variable/PretendardVariable.ttf",
                    LicenceName = Ofl,
                    LicenceUrl = OflUrl,
                    LicenceFileUrl = "https://raw.githubusercontent.com/orioncactus/pretendard/" +
                                     "main/LICENSE",
                    HomeUrl = "https://github.com/orioncactus/pretendard",
                    Note = "The variable Pretendard, weights 100–900 on one axis. Pretendard-Bold " +
                           "becomes wght 700 rather than its own face.",
                },

                // LINE Seed is openly licensed and is not openly hosted: the
                // fonts live behind a download page as a zip, and the GitHub
                // repository publishes sources rather than built files. Naming
                // the page is the honest answer; guessing a file URL is not.
                ["lineseed"] = LineSeed("LINE Seed"),
                ["lineseedsans"] = LineSeed("LINE Seed Sans"),
                ["lineseedkr"] = LineSeed("LINE Seed KR"),
                ["lineseedjp"] = LineSeed("LINE Seed JP"),
            };

        private static FontSourceCandidate Google(string family, string slug, string fileName,
            string remoteName, string note) =>
            new FontSourceCandidate
            {
                Match = FontSourceMatch.Download,
                FamilyName = family,
                FileName = fileName,
                DownloadUrl = GoogleFonts + slug + "/" + remoteName,
                LicenceName = Ofl,
                LicenceUrl = OflUrl,
                LicenceFileUrl = GoogleFonts + slug + "/OFL.txt",
                HomeUrl = "https://fonts.google.com/specimen/" + family.Replace(" ", "+"),
                Note = note,
            };

        private static FontSourceCandidate LineSeed(string family) => new FontSourceCandidate
        {
            Match = FontSourceMatch.Manual,
            FamilyName = family,
            LicenceName = Ofl,
            LicenceUrl = OflUrl,
            HomeUrl = "https://seed.line.me/index_en.html",
            Note = "Openly licensed, but published only as a zip behind a download page, so " +
                   "OneText will not fetch it. Download it there, unzip it, and drop the .ttf " +
                   "or .otf into the project.",
        };

        /// <summary>Every family the catalogue knows, for a test and for a doc.</summary>
        public static IEnumerable<string> Families
        {
            get
            {
                var seen = new List<string>();
                foreach (var entry in Entries.Values)
                    if (!seen.Contains(entry.FamilyName)) seen.Add(entry.FamilyName);
                return seen;
            }
        }

        /// <summary>
        /// What the catalogue knows about this family, or a candidate that says
        /// it knows nothing.
        ///
        /// Exact on the normalised name. "Cairo Play" is a different family from
        /// "Cairo" and comes back unmatched, which is the entire reason this is
        /// a dictionary lookup and not a fuzzy one.
        /// </summary>
        public static FontSourceCandidate Match(string familyName)
        {
            string key = Key(familyName);
            if (!string.IsNullOrEmpty(key) && Entries.TryGetValue(key, out var found)) return found;

            return new FontSourceCandidate
            {
                Match = FontSourceMatch.None,
                FamilyName = familyName,
                Note = "No catalogue entry for this family. It is either a commercial font, a " +
                       "font from an asset pack, or one this list has never been taught. Find " +
                       "the .ttf/.otf yourself and drop it into the project.",
            };
        }

        /// <summary>
        /// A family name reduced to the only thing worth comparing: its letters
        /// and digits, folded to lower case. Spaces, hyphens and capitalisation
        /// are how the same family gets written five ways; anything else is a
        /// different font.
        /// </summary>
        public static string Key(string familyName)
        {
            if (string.IsNullOrEmpty(familyName)) return string.Empty;

            var builder = new System.Text.StringBuilder(familyName.Length);
            foreach (char c in familyName)
                if (char.IsLetterOrDigit(c)) builder.Append(char.ToLowerInvariant(c));
            return builder.ToString();
        }

        // ------------------------------------------------------ the resolution

        /// <summary>
        /// Licence directories in the Google Fonts repository, probed in this
        /// order. Most of it is <c>ofl/</c>, and assuming that is how you offer
        /// somebody an Apache font under the wrong licence name.
        /// </summary>
        private static readonly string[] LicenceDirectories = { "ofl", "apache", "ufl" };

        /// <summary>
        /// The directory a family lives in inside <c>google/fonts</c>: its name
        /// with the separators taken out and the letters folded down, which is
        /// the convention the repository is laid out by — "Josefin Sans" is
        /// <c>josefinsans</c>, "Noto Sans KR" is <c>notosanskr</c>, "M PLUS 1p"
        /// is <c>mplus1p</c>.
        ///
        /// Empty when the name cannot be one of those directories at all: it is
        /// blank, it is a single character, or it contains something that is not
        /// an ASCII letter, digit or separator — a Korean or Japanese family
        /// name is a real font and is not a directory in this repository, and
        /// asking for one is a request that cannot succeed. A family that does
        /// not slugify comes back "find this yourself", which is a perfectly
        /// good answer.
        /// </summary>
        public static string Slug(string familyName)
        {
            if (string.IsNullOrEmpty(familyName)) return string.Empty;

            var builder = new System.Text.StringBuilder(familyName.Length);
            foreach (char c in familyName)
            {
                if (c >= 'a' && c <= 'z') builder.Append(c);
                else if (c >= 'A' && c <= 'Z') builder.Append((char)(c + ('a' - 'A')));
                else if (c >= '0' && c <= '9') builder.Append(c);
                else if (c == ' ' || c == '-' || c == '_' || c == '.' || c == '\'') continue;
                else return string.Empty;
            }

            return builder.Length >= 2 ? builder.ToString() : string.Empty;
        }

        /// <summary>
        /// Whether <see cref="Resolve"/> has anywhere to look, answered without
        /// a network. This is what decides whether the Hub draws the button, and
        /// it has to be free: it is asked once per row on a screen that may have
        /// eighteen rows.
        /// </summary>
        public static bool CanResolve(string familyName) =>
            !Match(familyName).Found && Slug(familyName).Length > 0;

        /// <summary>
        /// Looks one family up in the Google Fonts repository and comes back
        /// with something that can be shown before it is fetched.
        ///
        /// This makes a network request and must never be called from a scan,
        /// from a loop over a manifest, or from anything automatic. It is for
        /// one font, when somebody asks about that font. <see cref="Match"/> is
        /// the offline half and stays the only thing a scan may use.
        ///
        /// Two steps, because the file name cannot be guessed: the family name
        /// becomes a directory, and the directory's <c>METADATA.pb</c> names the
        /// files in it. The licence comes from which directory answered —
        /// <c>ofl/</c>, <c>apache/</c> or <c>ufl/</c> — confirmed against what
        /// the metadata declares, and a family whose licence cannot be
        /// established that way is offered as a manual download rather than as a
        /// button, because the point of the button is that the person pressing
        /// it has read the name of what they are agreeing to.
        /// </summary>
        public static FontSourceResolution Resolve(string familyName)
        {
            // The hand-written entry wins, and wins without a request. It says
            // things a repository cannot: that Pretendard is not Google's at
            // all, that LINE Seed is openly licensed and still not fetchable,
            // and what specifically will surprise you about this one font.
            var known = Match(familyName);
            if (known.Found)
            {
                return new FontSourceResolution
                {
                    Outcome = FontSourceOutcome.Ok,
                    Candidate = known,
                    Message = $"{known.FamilyName} is already in the catalogue by hand, which is a " +
                              "better answer than a lookup: nothing was fetched.",
                };
            }

            string slug = Slug(familyName);
            if (slug.Length == 0)
            {
                return new FontSourceResolution
                {
                    Outcome = FontSourceOutcome.NoCandidate,
                    Message = $"'{familyName}' cannot be a Google Fonts directory name, so there is " +
                              "nowhere to look it up. Find the .ttf/.otf yourself and drop it into " +
                              "the project.",
                };
            }

            try
            {
                foreach (string directory in LicenceDirectories)
                {
                    string url = GoogleRepo + directory + "/" + slug + "/METADATA.pb";
                    var fetched = Fetch(url, $"Looking up {familyName}…", out byte[] bytes);

                    if (fetched.Outcome == FontSourceOutcome.NotFound) continue;

                    if (fetched.Outcome != FontSourceOutcome.Ok)
                    {
                        // Offline, timed out or cancelled: probing the other two
                        // directories would be two more of the same wait for the
                        // same answer.
                        return new FontSourceResolution
                        {
                            Outcome = fetched.Outcome,
                            Message = fetched.Message,
                        };
                    }

                    string text;
                    try { text = System.Text.Encoding.UTF8.GetString(bytes); }
                    catch (System.Exception) { text = null; }

                    return Candidate(FontMetadata.Parse(text), directory, familyName);
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            return new FontSourceResolution
            {
                Outcome = FontSourceOutcome.NoCandidate,
                Message = $"Google Fonts publishes no family in a directory called '{slug}' — " +
                          $"{string.Join(", ", LicenceDirectories)} were all tried. It is either a " +
                          "commercial font, an asset-pack font, or one published somewhere else. " +
                          "Find the .ttf/.otf yourself and drop it into the project.",
            };
        }

        /// <summary>
        /// Turns one parsed <c>METADATA.pb</c> into something to show, or into a
        /// reason there is nothing to show.
        ///
        /// Separate from the request, and public, for the same reason
        /// <see cref="Install"/> is: every rule in the resolution lives here —
        /// which file, which licence, whether the two agree — and rules are
        /// worth testing without a network in front of them.
        /// </summary>
        public static FontSourceResolution Candidate(FontMetadata metadata, string licenceDirectory,
            string askedFor = null)
        {
            string family = string.IsNullOrEmpty(metadata.FamilyName) ? askedFor : metadata.FamilyName;
            string slug = Slug(family);

            if (!metadata.Found || slug.Length == 0)
            {
                return new FontSourceResolution
                {
                    Outcome = FontSourceOutcome.NoCandidate,
                    Message = $"The Google Fonts entry for '{askedFor ?? family}' does not name a " +
                              "family and a font file, so there is nothing to offer. Find the " +
                              ".ttf/.otf yourself and drop it into the project.",
                };
            }

            string page = "https://github.com/google/fonts/tree/main/" + licenceDirectory + "/" + slug;
            string specimen = "https://fonts.google.com/specimen/" + family.Replace(" ", "+");

            if (!LicenceOf(licenceDirectory, out string id, out string licenceName, out string licenceUrl,
                    out string licenceFile))
            {
                return Unfetchable(family, page,
                    $"{family} is published in google/fonts under '{licenceDirectory}/', which is not " +
                    "a licence this catalogue can name. Read the licence there before you use the " +
                    "font, then download it by hand.");
            }

            if (!string.IsNullOrEmpty(metadata.LicenceId) &&
                !string.Equals(metadata.LicenceId, id, System.StringComparison.OrdinalIgnoreCase))
            {
                // The directory and the metadata are two statements about the
                // same font, and a font whose publisher contradicts itself is
                // not one to fetch on somebody's behalf.
                return Unfetchable(family, page,
                    $"google/fonts keeps {family} in '{licenceDirectory}/' and its metadata says " +
                    $"'{metadata.LicenceId}'. Those disagree, so the licence is not established and " +
                    "OneText will not fetch it. Read the licence there and download it by hand.");
            }

            string remote = metadata.PreferredFile;
            if (string.IsNullOrEmpty(remote))
            {
                return Unfetchable(family, specimen, licenceName, licenceUrl,
                    $"google/fonts publishes {family} as {metadata.FileNames.Count:n0} separate " +
                    "static files and none of them is the Regular, so there is no one file to " +
                    "fetch. Pick the weight you had from the page and drop it into the project.");
            }

            string extension = Path.GetExtension(remote).ToLowerInvariant();
            if (extension != ".ttf" && extension != ".otf" && extension != ".ttc")
            {
                // WOFF and WOFF2 are refused here by name as well as by their
                // magic bytes later: knowing before the download that this
                // family is only published in a wrapper OneText cannot
                // rasterise from is thirty seconds and a puzzled user saved.
                return Unfetchable(family, specimen, licenceName, licenceUrl,
                    $"google/fonts publishes {family} as {extension}, which OneText cannot " +
                    "rasterise from — it draws from a .ttf or .otf. Find one of those.");
            }

            return new FontSourceResolution
            {
                Outcome = FontSourceOutcome.Ok,
                Message = $"{family} is in google/fonts under {licenceDirectory}/{slug}, as " +
                          $"{remote}, under the {licenceName}.",
                Candidate = new FontSourceCandidate
                {
                    Match = FontSourceMatch.Download,
                    Resolved = true,
                    FamilyName = family,
                    // The axis list is part of the published file name and is
                    // not part of a name a Unity project wants: square brackets
                    // in an asset path are a fight nobody signed up for.
                    FileName = WithoutAxes(remote),
                    DownloadUrl = GoogleRepo + licenceDirectory + "/" + slug + "/" + Encode(remote),
                    LicenceName = licenceName,
                    LicenceUrl = licenceUrl,
                    LicenceFileUrl = GoogleRepo + licenceDirectory + "/" + slug + "/" + licenceFile,
                    HomeUrl = specimen,
                    Note = metadata.Variable
                        ? $"This is the variable {family}: one file where the project had separate " +
                          "faces, with the weights as axes on it. OneText sets the weight each face " +
                          "was named for when it fills the placeholder, and clamps anything the " +
                          "file does not go up to."
                        : $"google/fonts publishes {family} as static files and this is the " +
                          "Regular. Other weights are separate files on the same page.",
                },
            };
        }

        /// <summary>
        /// A family that was identified and will not be fetched. Still worth
        /// every field it has: knowing the name, the page and — where it could
        /// be established — the licence is most of the work of finding the file
        /// by hand, which is the path this whole module is really about.
        /// </summary>
        private static FontSourceResolution Unfetchable(string family, string home, string note) =>
            Unfetchable(family, home, "not established", null, note);

        private static FontSourceResolution Unfetchable(string family, string home,
            string licenceName, string licenceUrl, string note) => new FontSourceResolution
        {
            Outcome = FontSourceOutcome.Ok,
            Message = note,
            Candidate = new FontSourceCandidate
            {
                Match = FontSourceMatch.Manual,
                Resolved = true,
                FamilyName = family,
                LicenceName = licenceName,
                LicenceUrl = licenceUrl,
                // Where the licence is what is in doubt, the repository
                // directory is the page to send somebody to: the licence file is
                // sitting in it. Where the file is what is in doubt, the
                // specimen page is where the other weights are.
                HomeUrl = home,
                Note = note,
            },
        };

        /// <summary>
        /// What a licence directory in google/fonts means, and which file in it
        /// holds the licence text. False for a directory this does not know,
        /// which is a candidate that becomes a manual download rather than a
        /// guess about somebody's terms.
        /// </summary>
        private static bool LicenceOf(string directory, out string id, out string name, out string url,
            out string fileName)
        {
            switch (directory)
            {
                case "ofl":
                    id = "OFL";
                    name = Ofl;
                    url = OflUrl;
                    fileName = "OFL.txt";
                    return true;
                case "apache":
                    id = "APACHE2";
                    name = "Apache License 2.0";
                    url = "https://www.apache.org/licenses/LICENSE-2.0";
                    fileName = "LICENSE.txt";
                    return true;
                case "ufl":
                    id = "UFL";
                    name = "Ubuntu Font Licence 1.0";
                    url = "https://ubuntu.com/legal/font-licence";
                    fileName = "UFL.txt";
                    return true;
                default:
                    id = null;
                    name = null;
                    url = null;
                    fileName = null;
                    return false;
            }
        }

        /// <summary>
        /// A published file name with its axis list removed:
        /// <c>Cairo[slnt,wght].ttf</c> is saved as <c>Cairo.ttf</c>, which is
        /// what the hand-written entries have always called it.
        /// </summary>
        public static string WithoutAxes(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return fileName;

            int open = fileName.IndexOf('[');
            if (open < 0) return fileName;
            int close = fileName.IndexOf(']', open + 1);
            if (close < 0) return fileName;

            return fileName.Substring(0, open) + fileName.Substring(close + 1);
        }

        /// <summary>
        /// Percent-encodes everything a URL path segment may not carry
        /// literally, which for these file names is the brackets and the comma:
        /// <c>Cairo[slnt,wght].ttf</c> becomes
        /// <c>Cairo%5Bslnt%2Cwght%5D.ttf</c>. Written out rather than delegated
        /// so that the hand-written entries and the resolved ones produce
        /// character-for-character the same URL.
        /// </summary>
        public static string Encode(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return fileName;

            var builder = new System.Text.StringBuilder(fileName.Length + 12);
            foreach (byte b in System.Text.Encoding.UTF8.GetBytes(fileName))
            {
                char c = (char)b;
                bool unreserved = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                                  (c >= '0' && c <= '9') ||
                                  c == '-' || c == '_' || c == '.' || c == '~';
                if (unreserved) builder.Append(c);
                else builder.Append('%').Append(b.ToString("X2"));
            }
            return builder.ToString();
        }

        // --------------------------------------------------------- the fetch

        /// <summary>
        /// Fetches one font, checks it, and writes it into the project.
        ///
        /// Called from a button, for one font, after its licence has been shown.
        /// Every way this can go wrong comes back as a
        /// <see cref="FontSourceDownload"/> with a sentence in it: a migration
        /// that aborted because a font server was down would be a worse tool
        /// than one that says the font server is down.
        /// </summary>
        public static FontSourceDownload Download(FontSourceCandidate candidate,
            string folder = null)
        {
            if (candidate.Match != FontSourceMatch.Download ||
                string.IsNullOrEmpty(candidate.DownloadUrl))
            {
                return new FontSourceDownload
                {
                    Outcome = FontSourceOutcome.NoCandidate,
                    Message = candidate.Match == FontSourceMatch.Manual
                        ? $"{candidate.FamilyName} has to be downloaded by hand from " +
                          $"{candidate.HomeUrl}."
                        : "There is nothing to download: the catalogue does not know this font.",
                };
            }

            try
            {
                var fetched = Fetch(candidate.DownloadUrl, $"Downloading {candidate.FamilyName}…",
                    out var fontBytes);
                if (fetched.Outcome != FontSourceOutcome.Ok) return fetched;

                // The licence is best-effort and never fatal: a font that
                // arrived with its OFL.txt missing is still the font, and the
                // licence name and URL were shown before any of this started.
                byte[] licenceBytes = null;
                if (!string.IsNullOrEmpty(candidate.LicenceFileUrl))
                {
                    var licence = Fetch(candidate.LicenceFileUrl,
                        $"Downloading the {candidate.FamilyName} licence…", out licenceBytes);
                    if (licence.Outcome != FontSourceOutcome.Ok) licenceBytes = null;
                }

                return Install(candidate, fontBytes, licenceBytes, folder);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        /// <summary>
        /// Checks bytes and writes them into the project. Separate from the
        /// request so that the checking — the part with a rule in it — can be
        /// tested without a network.
        /// </summary>
        public static FontSourceDownload Install(FontSourceCandidate candidate, byte[] fontBytes,
            byte[] licenceBytes, string folder = null)
        {
            if (!LooksLikeFont(fontBytes, out string what))
            {
                // Loudly, and before anything is written: a 404 page saved as
                // Cairo.ttf is a file that looks right in the Project window,
                // fails at rasterisation, and sends whoever hits it looking in
                // the wrong place entirely.
                string message = $"What came back from {candidate.DownloadUrl} is not a font " +
                                 $"file: {what}. Nothing was written into the project.";
                Debug.LogError($"OneText: {message}");
                return new FontSourceDownload
                {
                    Outcome = FontSourceOutcome.NotAFont,
                    Message = message,
                };
            }

            if (string.IsNullOrEmpty(candidate.FileName))
            {
                return new FontSourceDownload
                {
                    Outcome = FontSourceOutcome.NoCandidate,
                    Message = "These bytes are a font, and the catalogue said nothing about what " +
                              "to call it, so there is nowhere to put it.",
                };
            }

            folder = string.IsNullOrEmpty(folder) ? FontRecovery.RecoveryFolder : folder;
            if (!EnsureFolder(folder, out string folderError))
            {
                return new FontSourceDownload
                {
                    Outcome = FontSourceOutcome.WriteFailed,
                    Message = folderError,
                };
            }

            string fontPath = $"{folder}/{candidate.FileName}";
            string licencePath = null;
            try
            {
                File.WriteAllBytes(fontPath, fontBytes);

                if (licenceBytes != null && licenceBytes.Length > 0)
                {
                    // Saved beside the font and named after it, because in six
                    // months the question is which licence covers which file.
                    licencePath = $"{folder}/{Path.GetFileNameWithoutExtension(candidate.FileName)}" +
                                  " LICENSE.txt";
                    File.WriteAllBytes(licencePath, licenceBytes);
                }
            }
            catch (System.Exception error)
            {
                // A read-only project folder throws UnauthorizedAccessException
                // rather than IOException, and a font that arrived and could not
                // be saved is the same outcome either way.
                return new FontSourceDownload
                {
                    Outcome = FontSourceOutcome.WriteFailed,
                    Message = $"{candidate.FamilyName} downloaded, and writing it to {fontPath} " +
                              $"failed: {error.Message}",
                };
            }

            AssetDatabase.ImportAsset(fontPath, ImportAssetOptions.ForceSynchronousImport);
            if (licencePath != null) AssetDatabase.ImportAsset(licencePath);

            return new FontSourceDownload
            {
                Outcome = FontSourceOutcome.Ok,
                FontPath = fontPath,
                LicencePath = licencePath,
                Message = $"{candidate.FamilyName} ({what}, {fontBytes.Length / 1024:n0} KB) is at " +
                          $"{fontPath}, under the {candidate.LicenceName}" +
                          (licencePath == null
                              ? ". The source published no licence file to save with it."
                              : $", saved beside it as {Path.GetFileName(licencePath)}."),
            };
        }

        /// <summary>
        /// Whether these bytes begin like a font file, and what kind.
        ///
        /// Four bytes decide it. An sfnt starts with a version tag —
        /// <c>0x00010000</c> for TrueType outlines, <c>OTTO</c> for CFF ones,
        /// <c>ttcf</c> for a collection — and anything else is something else,
        /// most often an HTML error page served with a 200 because that is what
        /// content delivery networks do. WOFF and WOFF2 are recognised and
        /// refused rather than lumped in with the garbage: they are real fonts
        /// in a wrapper OneText cannot read, and the difference matters to
        /// whoever has to fix it.
        /// </summary>
        public static bool LooksLikeFont(byte[] bytes, out string what)
        {
            if (bytes == null || bytes.Length < 4)
            {
                what = bytes == null || bytes.Length == 0
                    ? "it is empty"
                    : $"it is {bytes.Length} bytes long";
                return false;
            }

            uint tag = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) |
                       ((uint)bytes[2] << 8) | bytes[3];

            switch (tag)
            {
                case 0x00010000:
                case 0x74727565: // 'true', Apple's older TrueType tag
                    what = "TrueType outlines";
                    return true;
                case 0x4F54544F: // 'OTTO'
                    what = "OpenType/CFF outlines";
                    return true;
                case 0x74746366: // 'ttcf'
                    what = "a TrueType collection";
                    return true;
                case 0x774F4646: // 'wOFF'
                    what = "it is a WOFF web font, which is a font in a wrapper OneText cannot " +
                           "rasterise from. Find the .ttf or .otf";
                    return false;
                case 0x774F4632: // 'wOF2'
                    what = "it is a WOFF2 web font, which is a font in a wrapper OneText cannot " +
                           "rasterise from. Find the .ttf or .otf";
                    return false;
            }

            what = LooksLikeMarkup(bytes)
                ? "it is an HTML page — most likely an error page, a login wall or a redirect " +
                  "served with a 200"
                : $"it begins 0x{tag:X8}, which is not any font signature";
            return false;
        }

        private static bool LooksLikeMarkup(byte[] bytes)
        {
            int limit = Mathf.Min(bytes.Length, 512);
            for (int i = 0; i < limit; i++)
            {
                if (bytes[i] != (byte)'<') continue;
                if (i + 1 < limit && (bytes[i + 1] == '!' || bytes[i + 1] == 'h' ||
                                      bytes[i + 1] == 'H' || bytes[i + 1] == '?')) return true;
            }
            return false;
        }

        /// <summary>
        /// One request, synchronous, with a progress bar somebody can cancel.
        ///
        /// Synchronous because this is an editor button and the caller wants an
        /// answer, and because the alternative — a coroutine in an editor
        /// window — buys asynchrony for a thirty-second worst case that already
        /// has a cancel button on it.
        /// </summary>
        private static FontSourceDownload Fetch(string url, string title, out byte[] data)
        {
            data = null;
            UnityWebRequest request = null;
            try
            {
                request = UnityWebRequest.Get(url);
                request.timeout = TimeoutSeconds;
                var operation = request.SendWebRequest();

                while (!operation.isDone)
                {
                    if (EditorUtility.DisplayCancelableProgressBar(title, url, operation.progress))
                    {
                        request.Abort();
                        return new FontSourceDownload
                        {
                            Outcome = FontSourceOutcome.Cancelled,
                            Message = "The download was cancelled. Nothing was written.",
                        };
                    }
                    System.Threading.Thread.Sleep(16);
                }

                switch (request.result)
                {
                    case UnityWebRequest.Result.Success:
                        data = request.downloadHandler.data;
                        return new FontSourceDownload { Outcome = FontSourceOutcome.Ok };

                    case UnityWebRequest.Result.ProtocolError:
                        return new FontSourceDownload
                        {
                            Outcome = FontSourceOutcome.NotFound,
                            Message = $"{url} answered {request.responseCode}. The catalogue's " +
                                      "address for this font is out of date, or the file moved; " +
                                      "download it by hand and drop it into the project.",
                        };

                    default:
                        bool timedOut = request.error != null &&
                                        request.error.IndexOf("timeout",
                                            System.StringComparison.OrdinalIgnoreCase) >= 0;
                        return new FontSourceDownload
                        {
                            Outcome = timedOut ? FontSourceOutcome.Timeout : FontSourceOutcome.Offline,
                            Message = timedOut
                                ? $"{url} did not answer within {TimeoutSeconds} seconds."
                                : $"{url} could not be reached: {request.error}. This machine may " +
                                  "be offline, or behind a proxy that blocks it.",
                        };
                }
            }
            catch (System.Exception error)
            {
                // Nothing a font download can throw is worth taking a migration
                // down with it.
                return new FontSourceDownload
                {
                    Outcome = FontSourceOutcome.Offline,
                    Message = $"{url} could not be requested at all: {error.Message}",
                };
            }
            finally
            {
                request?.Dispose();
            }
        }

        internal static bool EnsureFolder(string folder, out string error)
        {
            error = null;
            if (AssetDatabase.IsValidFolder(folder)) return true;

            var parts = folder.Split('/');
            string built = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = built + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                {
                    string guid = AssetDatabase.CreateFolder(built, parts[i]);
                    if (string.IsNullOrEmpty(guid))
                    {
                        error = $"The folder {next} could not be created, so there is nowhere to " +
                                "put the font.";
                        return false;
                    }
                }
                built = next;
            }
            return true;
        }
    }
}
