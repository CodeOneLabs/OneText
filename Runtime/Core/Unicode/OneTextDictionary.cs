using System.IO;
using System.IO.Compression;
using System.Text;
using UnityEngine;
using OneText.Unicode;
using CompressionLevel = System.IO.Compression.CompressionLevel;

namespace OneText
{
    /// <summary>
    /// A word list for one of the scripts that does not write spaces, stored in
    /// the project as an asset.
    ///
    /// <see cref="DictionaryLineBreaker"/> has always taken any newline-separated
    /// list; what it did not have was somewhere for one to live. A dictionary
    /// dropped in as a text file is a loose file somebody has to remember to
    /// load, in a build where nobody notices it was not loaded — the failure is
    /// wrapping that is subtly wrong in a language the team does not read. As an
    /// asset it is referenced by the project settings and installed before the
    /// first scene, like the prewarm charset beside it.
    ///
    /// The file is stored compressed, the way fonts are: ICU's Thai list is
    /// ~200 KB of highly repetitive UTF-8 and packs to a fraction of that. It is
    /// not vendored into the package — a project that never ships Thai should
    /// not carry it, and the licence notice belongs to the project that opts in,
    /// which is why <see cref="Notice"/> travels with the data.
    /// </summary>
    public sealed class OneTextDictionary : ScriptableObject
    {
        [SerializeField, HideInInspector] private byte[] _data;
        [SerializeField, HideInInspector] private bool _compressed;
        [SerializeField, HideInInspector] private int _uncompressedLength;

        [Tooltip("Script this list segments: Thai, Lao, Khmer or Myanmar.")]
        [SerializeField] private string _script = "Thai";

        [Tooltip("Where the list came from, so it can be updated later.")]
        [SerializeField] private string _sourcePath;

        [Tooltip("Attribution this dictionary's licence asks the shipping project to carry.")]
        [SerializeField, TextArea(2, 6)] private string _notice;

        [SerializeField, HideInInspector] private int _wordCount;

        private WordList _words;

        /// <summary>Script this list is registered for.</summary>
        public string Script => _script;

        /// <summary>Path of the file this asset was imported from.</summary>
        public string SourcePath => _sourcePath;

        /// <summary>Licence attribution to reproduce in the shipping project.</summary>
        public string Notice => _notice;

        /// <summary>Words in the list, counted at import.</summary>
        public int WordCount => _wordCount;

        /// <summary>Bytes the asset stores, after compression.</summary>
        public int StoredSize => _data?.Length ?? 0;

        /// <summary>Bytes of the original file.</summary>
        public int SourceSize => _uncompressedLength;

        /// <summary>The parsed trie, built once and shared.</summary>
        public WordList Words
        {
            get
            {
                if (_words != null) return _words;
                string text = GetText();
                if (string.IsNullOrEmpty(text)) return null;
                _words = new WordList();
                _words.AddAll(text);
                return _words;
            }
        }

        /// <summary>Registers this list for its script, replacing any previous one.</summary>
        public void Install()
        {
            var words = Words;
            if (words != null && !string.IsNullOrEmpty(_script))
                DictionaryLineBreaker.SetWordList(_script, words);
        }

        /// <summary>The word list as text, decompressed on demand.</summary>
        public string GetText()
        {
            if (_data == null || _data.Length == 0) return null;
            if (!_compressed) return Encoding.UTF8.GetString(_data);

            using var input = new MemoryStream(_data);
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);
            var output = new byte[_uncompressedLength];
            int read = 0;
            while (read < output.Length)
            {
                int chunk = deflate.Read(output, read, output.Length - read);
                if (chunk <= 0) break;
                read += chunk;
            }
            return Encoding.UTF8.GetString(output, 0, read);
        }

        /// <summary>
        /// Fills the asset from a word-list file. Editor-side setup.
        ///
        /// Deflate rather than the brotli fonts use: a word list is a hundredth
        /// of a font's size, so the packing time brotli buys back on a 55 MB
        /// face buys nothing here, and deflate is what every platform already
        /// has.
        /// </summary>
        public void Initialize(string wordList, string script, string sourcePath, string notice)
        {
            _script = string.IsNullOrEmpty(script) ? "Thai" : script;
            _sourcePath = sourcePath;
            _notice = notice;
            _words = null;

            var bytes = Encoding.UTF8.GetBytes(wordList ?? string.Empty);
            _uncompressedLength = bytes.Length;

            using var output = new MemoryStream();
            using (var packer = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
                packer.Write(bytes, 0, bytes.Length);
            var compressed = output.ToArray();

            if (compressed.Length < bytes.Length)
            {
                _data = compressed;
                _compressed = true;
            }
            else
            {
                _data = bytes;
                _compressed = false;
            }

            var counter = new WordList();
            counter.AddAll(wordList);
            _wordCount = counter.WordCount;
        }

        private void OnDisable() => _words = null;
    }
}
