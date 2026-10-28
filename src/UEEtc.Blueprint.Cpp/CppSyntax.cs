using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using UEEtc.Blueprint;

namespace UEEtc.Blueprint.Cpp;

internal static class CppSyntax
{
    public static string Name(string value) => Regex.Replace(value, "[^A-Za-z0-9_]", "_").Trim('_') is { Length: > 0 } name ? name[..Math.Min(60, name.Length)] : "Unnamed";
    public static string Quote(string value)
    {
        var text = new StringBuilder("TEXT(\"");
        foreach (char c in value)
            text.Append(c switch { '\\' => "\\\\", '"' => "\\\"", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t", _ when char.IsControl(c) => "?", _ => c.ToString() });
        return text.Append("\")").ToString();
    }
    public static string? Type(IReadOnlyDictionary<string, string> fields, string prefix = "")
    {
        string Get(string key) => UnrealTextParser.Value(fields, prefix + key);
        if (Get("ContainerType") is not ("" or "None") || Get("bIsReference") == "True" || Get("bIsWeakPointer") == "True") return null;
        return Get("PinCategory") switch
        {
            "bool" => "bool", "int" => "int32", "int64" => "int64",
            "real" => Get("PinSubCategory") == "float" ? "float" : "double",
            "float" => "float", "double" => "double", "name" => "FName", "string" => "FString",
            _ => null
        };
    }
    public static string Literal(string type, string value)
    {
        // Consume escape pairs so a literal backslash is not interpreted a second time.
        var decoded = new StringBuilder();
        for (int i = 0; i < value.Length; ++i)
        {
            char c = value[i];
            if (c == '\\')
            {
                if (++i == value.Length) throw new InvalidDataException("不正な文字列エスケープです。");
                c = value[i] switch { '\\' => '\\', '"' => '"', '\'' => '\'', 'n' => '\n', 'r' => '\r', 't' => '\t', _ => throw new InvalidDataException("未対応の文字列エスケープです。") };
            }
            if (char.IsControl(c) && c is not ('\n' or '\r' or '\t')) throw new InvalidDataException("未対応の制御文字です。");
            decoded.Append(c);
        }
        value = decoded.ToString();
        if (type is "FString" or "FName") return type + "(" + Quote(value) + ")";
        if (value.Length == 0) return type == "bool" ? "false" : "0";
        if (type == "bool" && bool.TryParse(value, out bool boolean)) return boolean ? "true" : "false";
        if (type == "int32" && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int integer)) return integer.ToString(CultureInfo.InvariantCulture);
        if (type == "int64" && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long large)) return large == long.MinValue ? "(-9223372036854775807LL - 1LL)" : large.ToString(CultureInfo.InvariantCulture) + "LL";
        if (type is "double" or "float" && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double real) && double.IsFinite(real))
        {
            if (type == "float" && !float.IsFinite((float)real)) throw new InvalidDataException("floatの範囲を超える既定値です。");
            string number = real.ToString("R", CultureInfo.InvariantCulture);
            return number + (!number.Contains('.') && !number.Contains('E') ? ".0" : "") + (type == "float" ? "f" : "");
        }
        throw new InvalidDataException("未対応または不正な既定値: " + type);
    }
}
