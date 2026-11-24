using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace WeightCheckerCS.Core
{
    /// <summary>极简 JSON 读写：对象 → Json.Obj（保持键顺序），数组 → List&lt;object&gt;，数字 → double。</summary>
    public static class Json
    {
        public static object Parse(string text)
        {
            var p = new Parser(text);
            p.SkipWs();
            object v = p.ReadValue();
            p.SkipWs();
            if (!p.End) throw p.Error("JSON 结尾有多余内容");
            return v;
        }

        public static string Write(object value)
        {
            var sb = new StringBuilder();
            WriteValue(sb, value, 0);
            sb.Append("\r\n");
            return sb.ToString();
        }

        private static void Indent(StringBuilder sb, int level)
        {
            sb.Append("\r\n").Append(' ', level * 2);
        }

        private static void WriteValue(StringBuilder sb, object v, int level)
        {
            if (v == null) { sb.Append("null"); return; }
            if (v is string) { WriteString(sb, (string)v); return; }
            if (v is bool) { sb.Append((bool)v ? "true" : "false"); return; }
            if (v is double || v is float || v is decimal)
            {
                double d = Convert.ToDouble(v, CultureInfo.InvariantCulture);
                if (double.IsNaN(d) || double.IsInfinity(d)) throw new ArgumentException("JSON 不支持 NaN/Infinity");
                if (d == Math.Floor(d) && Math.Abs(d) < 1e15) sb.Append(((long)d).ToString(CultureInfo.InvariantCulture));
                else sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            if (v is int || v is long || v is short || v is byte || v is uint)
            {
                sb.Append(Convert.ToInt64(v, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
                return;
            }
            var dict = v as IDictionary;
            if (dict != null)
            {
                sb.Append('{');
                bool first = true;
                foreach (DictionaryEntry e in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    Indent(sb, level + 1);
                    WriteString(sb, Convert.ToString(e.Key, CultureInfo.InvariantCulture));
                    sb.Append(": ");
                    WriteValue(sb, e.Value, level + 1);
                }
                if (!first) Indent(sb, level);
                sb.Append('}');
                return;
            }
            var list = v as IEnumerable;
            if (list != null)
            {
                sb.Append('[');
                bool first = true;
                foreach (object item in list)
                {
                    if (!first) sb.Append(", ");
                    first = false;
                    WriteValue(sb, item, level + 1);
                }
                sb.Append(']');
                return;
            }
            throw new ArgumentException("不支持的 JSON 类型: " + v.GetType().Name);
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        /// <summary>保持插入顺序的 JSON 对象。</summary>
        public sealed class Obj : IDictionary
        {
            private readonly List<string> _keys = new List<string>();
            private readonly Dictionary<string, object> _map = new Dictionary<string, object>();
            /// <summary>解析时出现过的重复键（后者覆盖前者）。</summary>
            public readonly List<string> DuplicateKeys = new List<string>();

            public object this[string key]
            {
                get { object v; return _map.TryGetValue(key, out v) ? v : null; }
                set { if (!_map.ContainsKey(key)) _keys.Add(key); _map[key] = value; }
            }
            public bool Has(string key) { return _map.ContainsKey(key); }
            public IList<string> Keys { get { return _keys; } }
            public bool Remove(string key) { _keys.Remove(key); return _map.Remove(key); }

            object IDictionary.this[object key] { get { return this[(string)key]; } set { this[(string)key] = value; } }
            ICollection IDictionary.Keys { get { return _keys.ToArray(); } }
            ICollection IDictionary.Values { get { var l = new List<object>(); foreach (var k in _keys) l.Add(_map[k]); return l; } }
            bool IDictionary.IsReadOnly { get { return false; } }
            bool IDictionary.IsFixedSize { get { return false; } }
            void IDictionary.Add(object key, object value) { this[(string)key] = value; }
            void IDictionary.Clear() { _keys.Clear(); _map.Clear(); }
            bool IDictionary.Contains(object key) { return _map.ContainsKey((string)key); }
            void IDictionary.Remove(object key) { Remove((string)key); }
            public int Count { get { return _keys.Count; } }
            object ICollection.SyncRoot { get { return this; } }
            bool ICollection.IsSynchronized { get { return false; } }
            void ICollection.CopyTo(Array array, int index) { throw new NotSupportedException(); }
            IDictionaryEnumerator IDictionary.GetEnumerator() { return new Enumerator(this); }
            IEnumerator IEnumerable.GetEnumerator() { return new Enumerator(this); }

            private sealed class Enumerator : IDictionaryEnumerator
            {
                private readonly Obj _o; private int _i = -1;
                public Enumerator(Obj o) { _o = o; }
                public DictionaryEntry Entry { get { var k = _o._keys[_i]; return new DictionaryEntry(k, _o._map[k]); } }
                public object Key { get { return _o._keys[_i]; } }
                public object Value { get { return _o._map[_o._keys[_i]]; } }
                public object Current { get { return Entry; } }
                public bool MoveNext() { return ++_i < _o._keys.Count; }
                public void Reset() { _i = -1; }
            }
        }

        private sealed class Parser
        {
            private readonly string _s;
            private int _i;
            public Parser(string s) { _s = s ?? ""; if (_s.Length > 0 && _s[0] == '\uFEFF') _i = 1; }
            public bool End { get { return _i >= _s.Length; } }

            public FormatException Error(string msg)
            {
                int line = 1;
                for (int k = 0; k < _i && k < _s.Length; k++) if (_s[k] == '\n') line++;
                return new FormatException(msg + "（第 " + line + " 行）");
            }

            public void SkipWs()
            {
                while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++;
            }

            public object ReadValue()
            {
                SkipWs();
                if (End) throw Error("JSON 意外结束");
                char c = _s[_i];
                if (c == '{') return ReadObject();
                if (c == '[') return ReadArray();
                if (c == '"') return ReadString();
                if (Match("true")) return true;
                if (Match("false")) return false;
                if (Match("null")) return null;
                return ReadNumber();
            }

            private bool Match(string word)
            {
                if (_i + word.Length <= _s.Length && string.CompareOrdinal(_s, _i, word, 0, word.Length) == 0) { _i += word.Length; return true; }
                return false;
            }

            private Obj ReadObject()
            {
                var o = new Obj();
                _i++;
                SkipWs();
                if (!End && _s[_i] == '}') { _i++; return o; }
                while (true)
                {
                    SkipWs();
                    if (End || _s[_i] != '"') throw Error("应为字符串键");
                    string key = ReadString();
                    SkipWs();
                    if (End || _s[_i] != ':') throw Error("应为 ':'");
                    _i++;
                    if (o.Has(key)) o.DuplicateKeys.Add(key);
                    o[key] = ReadValue();
                    SkipWs();
                    if (End) throw Error("对象未结束");
                    if (_s[_i] == ',') { _i++; continue; }
                    if (_s[_i] == '}') { _i++; return o; }
                    throw Error("应为 ',' 或 '}'");
                }
            }

            private List<object> ReadArray()
            {
                var l = new List<object>();
                _i++;
                SkipWs();
                if (!End && _s[_i] == ']') { _i++; return l; }
                while (true)
                {
                    l.Add(ReadValue());
                    SkipWs();
                    if (End) throw Error("数组未结束");
                    if (_s[_i] == ',') { _i++; continue; }
                    if (_s[_i] == ']') { _i++; return l; }
                    throw Error("应为 ',' 或 ']'");
                }
            }

            private string ReadString()
            {
                var sb = new StringBuilder();
                _i++;
                while (true)
                {
                    if (End) throw Error("字符串未结束");
                    char c = _s[_i++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\') { sb.Append(c); continue; }
                    if (End) throw Error("转义未结束");
                    char e = _s[_i++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (_i + 4 > _s.Length) throw Error("\\u 转义不完整");
                            sb.Append((char)int.Parse(_s.Substring(_i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            _i += 4;
                            break;
                        default: throw Error("未知转义 \\" + e);
                    }
                }
            }

            private object ReadNumber()
            {
                int start = _i;
                while (_i < _s.Length && "+-0123456789.eE".IndexOf(_s[_i]) >= 0) _i++;
                if (start == _i) throw Error("无法识别的值");
                double d;
                if (!double.TryParse(_s.Substring(start, _i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                    throw Error("数字格式错误");
                return d;
            }
        }
    }
}
