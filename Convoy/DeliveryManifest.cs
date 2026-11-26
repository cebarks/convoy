using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Convoy
{
    public sealed class DeliveryAsset
    {
        public string Path { get; }
        public long Length { get; }
        public string Sha256 { get; }
        public string Xxh3_64 { get; }

        internal DeliveryAsset(string path, long length, string sha256, string xxh3)
        { Path = path; Length = length; Sha256 = sha256; Xxh3_64 = xxh3; }

        internal bool Same(DeliveryAsset other) => Length == other.Length && Sha256 == other.Sha256 && Xxh3_64 == other.Xxh3_64;
    }

    public sealed class CatalogMod
    {
        public long Id { get; }
        public long? ForgeId { get; }
        public string? Guid { get; }
        public string Name { get; }
        public string Version { get; }
        public IReadOnlyList<DeliveryAsset> Files { get; }
        public IReadOnlyList<DeliveryAsset> Bundles { get; }
        internal IEnumerable<DeliveryAsset> Assets => Files.Concat(Bundles);

        internal CatalogMod(long id, long? forgeId, string? guid, string name, string version,
            IReadOnlyList<DeliveryAsset> files, IReadOnlyList<DeliveryAsset> bundles)
        {
            Id = id; ForgeId = forgeId; Guid = guid; Name = name; Version = version;
            Files = files; Bundles = bundles;
        }
    }

    public sealed class CatalogGroup
    {
        public string Slug { get; }
        public string Name { get; }
        public string Tier { get; }
        public IReadOnlyList<CatalogMod> Mods { get; }

        internal CatalogGroup(string slug, string name, string tier, IReadOnlyList<CatalogMod> mods)
        { Slug = slug; Name = name; Tier = tier; Mods = mods; }
    }

    public sealed class DeliveryManifest
    {
        public string Generation { get; }
        public string SptVersion { get; }
        public string QuartermasterVersion { get; }
        public IReadOnlyList<CatalogGroup> Groups { get; }
        public IReadOnlyList<string> Exclusions { get; }
        internal Uri Origin { get; }

        private DeliveryManifest(Uri origin, string generation, string sptVersion, string quartermasterVersion,
            IReadOnlyList<CatalogGroup> groups, IReadOnlyList<string> exclusions)
        {
            Origin = origin; Generation = generation; SptVersion = sptVersion; QuartermasterVersion = quartermasterVersion;
            Groups = groups; Exclusions = exclusions;
        }

        internal static bool Hex(string value, int length) => value.Length == length && value.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));
        internal static string Tag(string digest) => "\"" + digest + "\"";
        private static InvalidDataException Invalid(string field) => new InvalidDataException("Invalid Convoy v2 catalog: " + field);
        private static JToken Field(JToken value, string name) => (value as JObject)?[name] ?? throw Invalid(name);
        private static string Text(JToken value) => value.Type == JTokenType.String ? (string)value! : throw Invalid("expected string");
        private static long Integer(JToken value) => value.Type == JTokenType.Integer && ((JValue)value).Value is long n ? n : throw Invalid("expected signed 64-bit integer");
        private static JArray ArrayField(JToken value, string name) => Field(value, name) as JArray ?? throw Invalid(name);

        // JsonTextReader supports extensions such as comments, unquoted property names,
        // single quotes and octal integers. Admit only JSON tokens before parsing their shape.
        private static void CheckJsonTokens(string json)
        {
            var i = 0;
            var previous = '\0';
            char Peek() => i == json.Length ? '\0' : json[i];
            bool Digit() => Peek() >= '0' && Peek() <= '9';
            void Digits()
            {
                if (!Digit()) throw Invalid("malformed number");
                do { i++; } while (Digit());
            }
            void Literal(string value)
            {
                foreach (var c in value)
                    if (Peek() != c) throw Invalid("malformed literal");
                    else i++;
            }

            while (i < json.Length)
            {
                var c = json[i++];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n') continue;
                switch (c)
                {
                    case '}': case ']':
                        if (previous == ',') throw Invalid("trailing comma");
                        break;
                    case '{': case '[': case ',': case ':':
                        break;
                    case '"':
                        while (Peek() != '"')
                        {
                            if (Peek() < ' ') throw Invalid("malformed string");
                            if (json[i++] != '\\') continue;
                            var escape = Peek();
                            if (escape == '\0') throw Invalid("unterminated escape");
                            i++;
                            if (escape == 'u')
                            {
                                for (var h = 0; h < 4; h++)
                                    if (Uri.IsHexDigit(Peek())) i++;
                                    else throw Invalid("malformed Unicode escape");
                            }
                            else if ("\"\\/bfnrt".IndexOf(escape) < 0) throw Invalid("malformed escape");
                        }
                        i++;
                        break;
                    case 't': Literal("rue"); break;
                    case 'f': Literal("alse"); break;
                    case 'n': Literal("ull"); break;
                    default:
                        if (c != '-') i--;
                        if (Peek() == '0')
                        {
                            i++;
                            if (Digit()) throw Invalid("leading zero");
                        }
                        else Digits();
                        if (Peek() == '.') { i++; Digits(); }
                        if (Peek() == 'e' || Peek() == 'E')
                        {
                            i++;
                            if (Peek() == '+' || Peek() == '-') i++;
                            Digits();
                        }
                        break;
                }
                previous = c;
            }
        }

        internal static DeliveryManifest Parse(Uri origin, string json, string etag)
        {
            CheckJsonTokens(json);
            using var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None, MaxDepth = 32 };
            var root = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (reader.Read() || Integer(Field(root, "protocol")) != 2) throw Invalid("protocol");
            var generation = Text(Field(root, "generation"));
            if (!Hex(generation, 64) || Tag(generation) != etag) throw Invalid("generation / ETag disagreement");
            var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var assets = new Dictionary<string, DeliveryAsset>(StringComparer.OrdinalIgnoreCase);
            var digests = new Dictionary<string, DeliveryAsset>(StringComparer.Ordinal);
            var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ids = new HashSet<long>();
            var slugs = new HashSet<string>(StringComparer.Ordinal);

            DeliveryAsset Asset(JToken token, HashSet<string> own, bool bundle)
            {
                var path = Text(Field(token, "path"));
                var length = Integer(Field(token, "length"));
                var sha = Text(Field(token, "sha256"));
                var xxh = Text(Field(token, "xxh3_64"));
                if (length < 0 || !Hex(sha, 64) || !Hex(xxh, 16) || !SyncFiles.Allowed(path)
                    || path.Equals("BepInEx/config", StringComparison.OrdinalIgnoreCase) || SyncFiles.Private(path) || SyncFiles.Bundle(path) != bundle || !own.Add(path)) throw Invalid(path);
                var asset = new DeliveryAsset(path, length, sha, xxh);
                if (directories.Contains(path) || (assets.TryGetValue(path, out var previous) && !previous.Same(asset))
                    || (digests.TryGetValue(sha, out var sameDigest) && !sameDigest.Same(asset))) throw Invalid(path);
                for (var end = path.Length; end > 0;)
                {
                    var prefix = path.Substring(0, end);
                    if (paths.TryGetValue(prefix, out var spelling) && spelling != prefix) throw Invalid(path);
                    paths[prefix] = prefix;
                    end = path.LastIndexOf('/', end - 1);
                    if (end >= 0)
                    {
                        var parent = path.Substring(0, end);
                        if (assets.ContainsKey(parent)) throw Invalid(path);
                        directories.Add(parent);
                    }
                }
                assets[path] = digests[sha] = asset;
                return asset;
            }

            CatalogMod Mod(JToken token)
            {
                var id = Integer(Field(token, "id"));
                if (id <= 0 || !ids.Add(id)) throw Invalid("mod ID " + id);
                var forge = Field(token, "forge_id");
                var guid = Field(token, "guid");
                var own = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                return new CatalogMod(id, forge.Type == JTokenType.Null ? (long?)null : Integer(forge),
                    guid.Type == JTokenType.Null ? null : Text(guid), Text(Field(token, "name")), Text(Field(token, "version")),
                    ArrayField(token, "files").Select(a => Asset(a, own, false)).ToList().AsReadOnly(),
                    ArrayField(token, "bundles").Select(a => Asset(a, own, true)).ToList().AsReadOnly());
            }

            CatalogGroup Group(JToken token)
            {
                var slug = Text(Field(token, "slug"));
                var tier = Text(Field(token, "tier"));
                if (slug.Length == 0 || !slugs.Add(slug) || (tier != "required" && tier != "optional")) throw Invalid("group " + slug);
                return new CatalogGroup(slug, Text(Field(token, "name")), tier,
                    ArrayField(token, "mods").Select(Mod).ToList().AsReadOnly());
            }

            return new DeliveryManifest(origin, generation, Text(Field(root, "spt_version")), Text(Field(root, "quartermaster_version")),
                ArrayField(root, "groups").Select(Group).ToList().AsReadOnly(), ArrayField(root, "exclusions").Select(Text).ToList().AsReadOnly());
        }
    }
}
