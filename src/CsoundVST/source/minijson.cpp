//------------------------------------------------------------------------------
// Csound Visual VST3 - minimal JSON (implementation)
//
// Serializer: compact output, escapes only what JSON requires, passes UTF-8
// through. Parser: recursive descent over the UTF-8 byte string, depth-limited
// (200 levels) so a malicious or broken request cannot overflow the IPC
// thread's stack. The parser is slightly more lenient than RFC 8259 (see the
// notes on str() and number parsing below); it never reads out of bounds.
//------------------------------------------------------------------------------
#include "minijson.h"

#include <charconv> // std::to_chars / std::from_chars: locale-independent number conversion
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <system_error> // std::errc

namespace CsoundVisual {
namespace Json {

//------------------------------------------------------------------------------
Value& Value::push (Value v)
{
	if (type != Type::Array)
	{
		*this = array ();
	}
	items.push_back (std::move (v));
	return items.back ();
}

//------------------------------------------------------------------------------
Value& Value::set (const std::string& key, Value v)
{
	if (type != Type::Object)
		*this = object ();
	for (auto& m : members)
	{
		if (m.first == key)
		{
			m.second = std::move (v);
			return m.second;
		}
	}
	members.emplace_back (key, std::move (v));
	return members.back ().second;
}

//------------------------------------------------------------------------------
/// Shared immutable null returned by failed lookups, so operator[] and at() can return a
/// reference without allocating. Function-local static: thread-safe initialisation.
static const Value& nullValue ()
{
	static const Value n;
	return n;
}

const Value& Value::operator[] (const std::string& key) const
{
	if (type == Type::Object)
		for (auto& m : members)
			if (m.first == key)
				return m.second;
	return nullValue ();
}

bool Value::has (const std::string& key) const
{
	if (type == Type::Object)
		for (auto& m : members)
			if (m.first == key)
				return true;
	return false;
}

const Value& Value::at (size_t i) const
{
	if (type == Type::Array && i < items.size ())
		return items[i];
	return nullValue ();
}

//------------------------------------------------------------------------------
std::string quote (const std::string& s)
{
	std::string out;
	out.reserve (s.size () + 2);
	out += '"';
	// Iterate as unsigned char so bytes >= 0x80 compare correctly against 0x20 below
	// (plain char is signed on MSVC).
	for (unsigned char c : s)
	{
		switch (c)
		{
			case '"': out += "\\\""; break;
			case '\\': out += "\\\\"; break;
			case '\b': out += "\\b"; break;
			case '\f': out += "\\f"; break;
			case '\n': out += "\\n"; break;
			case '\r': out += "\\r"; break;
			case '\t': out += "\\t"; break;
			default:
				if (c < 0x20)
				{
					char buf[8];
					std::snprintf (buf, sizeof (buf), "\\u%04x", c);
					out += buf;
				}
				else
					out += (char)c; // UTF-8 bytes pass through unchanged
		}
	}
	out += '"';
	return out;
}

//------------------------------------------------------------------------------
std::string Value::dump () const
{
	std::string out;
	dumpTo (out);
	return out;
}

void Value::dumpTo (std::string& out) const
{
	switch (type)
	{
		case Type::Null: out += "null"; break;
		case Type::Bool: out += boolean ? "true" : "false"; break;
		case Type::Number:
		{
			if (!std::isfinite (number))
			{
				out += "null"; // JSON has no NaN/Inf
				break;
			}
			// Integral values print as integers ("48000", not "48000.0" or "4.8e+04"), which
			// keeps replies readable and lets clients parse them as ints. Below 1e15 every
			// integral double is exact, so fixed notation with no fraction is lossless.
			// Everything else uses the shortest representation that round-trips exactly.
			// std::to_chars is used instead of snprintf("%.17g") because it is
			// locale-independent: snprintf honours the C locale's decimal separator, so a host
			// (or another plug-in in the same process) that set LC_NUMERIC to a comma locale
			// made the reply invalid JSON ("0,5"). to_chars always writes '.'.
			// 40 bytes: fixed notation of |x| < 1e15 is at most 16 chars, the shortest general
			// form of any double at most 24 chars ("-2.2250738585072014e-308").
			char buf[40];
			std::to_chars_result r;
			if (number == std::floor (number) && std::fabs (number) < 1e15)
				r = std::to_chars (buf, buf + sizeof (buf), number, std::chars_format::fixed);
			else
				r = std::to_chars (buf, buf + sizeof (buf), number); // shortest round-trip
			if (r.ec == std::errc ())
				out.append (buf, r.ptr);
			else
				out += "null"; // cannot happen with a 40-byte buffer; keep the JSON valid anyway
			break;
		}
		case Type::String: out += quote (string); break;
		case Type::Array:
		{
			out += '[';
			for (size_t i = 0; i < items.size (); ++i)
			{
				if (i)
					out += ',';
				items[i].dumpTo (out);
			}
			out += ']';
			break;
		}
		case Type::Object:
		{
			out += '{';
			for (size_t i = 0; i < members.size (); ++i)
			{
				if (i)
					out += ',';
				out += quote (members[i].first);
				out += ':';
				members[i].second.dumpTo (out);
			}
			out += '}';
			break;
		}
	}
}

//------------------------------------------------------------------------------
// Recursive-descent parser
//------------------------------------------------------------------------------
namespace {

/// Parser state over one input string. Every member function returns false on error after
/// recording the first error message (with byte offset) in `error`.
struct Parser
{
	const std::string& s; ///< input text (UTF-8); must outlive the parser
	size_t p = 0;         ///< current byte offset into s
	std::string error;    ///< first error encountered ("" while none)
	int depth = 0;        ///< current nesting depth of value() calls

	explicit Parser (const std::string& text) : s (text) {}

	/// Skips JSON whitespace (space, tab, LF, CR - exactly the four RFC 8259 allows).
	void ws ()
	{
		while (p < s.size () && (s[p] == ' ' || s[p] == '\t' || s[p] == '\n' || s[p] == '\r'))
			++p;
	}

	/// Records `msg` plus the current offset (only the first error is kept, since inner
	/// failures propagate outwards) and returns false for `return fail(...)` convenience.
	bool fail (const char* msg)
	{
		if (error.empty ())
			error = std::string (msg) + " at offset " + std::to_string (p);
		return false;
	}

	/// Encodes Unicode code point `cp` (<= 0x10FFFF, never a surrogate - str() guarantees
	/// both) as 1-4 UTF-8 bytes:
	///   U+0000..U+007F    0xxxxxxx
	///   U+0080..U+07FF    110xxxxx 10xxxxxx
	///   U+0800..U+FFFF    1110xxxx 10xxxxxx 10xxxxxx
	///   U+10000..U+10FFFF 11110xxx 10xxxxxx 10xxxxxx 10xxxxxx
	/// Note: \u0000 produces an embedded NUL byte in the std::string (legal, but C APIs
	/// that receive c_str() will see the string end there).
	static void appendUtf8 (std::string& out, unsigned cp)
	{
		if (cp < 0x80)
			out += (char)cp;
		else if (cp < 0x800)
		{
			out += (char)(0xC0 | (cp >> 6));
			out += (char)(0x80 | (cp & 0x3F));
		}
		else if (cp < 0x10000)
		{
			out += (char)(0xE0 | (cp >> 12));
			out += (char)(0x80 | ((cp >> 6) & 0x3F));
			out += (char)(0x80 | (cp & 0x3F));
		}
		else
		{
			out += (char)(0xF0 | (cp >> 18));
			out += (char)(0x80 | ((cp >> 12) & 0x3F));
			out += (char)(0x80 | ((cp >> 6) & 0x3F));
			out += (char)(0x80 | (cp & 0x3F));
		}
	}

	/// Reads exactly four hex digits (either case) at p into v (0..0xFFFF), advancing p.
	/// Used for the XXXX of a \uXXXX escape; p must already be past the "\u".
	bool hex4 (unsigned& v)
	{
		if (p + 4 > s.size ())
			return fail ("truncated \\u escape");
		v = 0;
		for (int i = 0; i < 4; ++i)
		{
			char c = s[p++];
			v <<= 4;
			if (c >= '0' && c <= '9')
				v |= c - '0';
			else if (c >= 'a' && c <= 'f')
				v |= c - 'a' + 10;
			else if (c >= 'A' && c <= 'F')
				v |= c - 'A' + 10;
			else
				return fail ("bad hex digit");
		}
		return true;
	}

	/// Parses a string literal starting at the opening quote and appends the decoded UTF-8
	/// text to `out`. Decoding rules:
	///   * the eight short escapes \" \\ \/ \b \f \n \r \t;
	///   * \uXXXX -> code point encoded as UTF-8; a high surrogate (D800-DBFF) immediately
	///     followed by \u + low surrogate (DC00-DFFF) combines into one supplementary code
	///     point (U+10000 + (hi - D800) * 0x400 + (lo - DC00)); a lone or mismatched
	///     surrogate becomes U+FFFD instead of producing invalid UTF-8 (when a high
	///     surrogate is followed by a non-low \u escape, only the high surrogate becomes
	///     U+FFFD and the second escape is then decoded normally);
	///   * any other escape letter is an error.
	/// Lenient on purpose: raw control characters (< 0x20) inside the string are accepted
	/// and raw bytes are copied as-is without UTF-8 validation.
	bool str (std::string& out)
	{
		if (p >= s.size () || s[p] != '"')
			return fail ("expected string");
		++p;
		while (p < s.size ())
		{
			char c = s[p++];
			if (c == '"')
				return true;
			if (c == '\\')
			{
				if (p >= s.size ())
					break; // backslash at end of input -> "unterminated string"
				char e = s[p++];
				switch (e)
				{
					case '"': out += '"'; break;
					case '\\': out += '\\'; break;
					case '/': out += '/'; break;
					case 'b': out += '\b'; break;
					case 'f': out += '\f'; break;
					case 'n': out += '\n'; break;
					case 'r': out += '\r'; break;
					case 't': out += '\t'; break;
					case 'u':
					{
						unsigned cp;
						if (!hex4 (cp))
							return false;
						if (cp >= 0xD800 && cp <= 0xDBFF)
						{
							// High surrogate: expect a low surrogate next.
							if (p + 1 < s.size () && s[p] == '\\' && s[p + 1] == 'u')
							{
								// Remember where the second escape starts: if it turns out not to
								// be a low surrogate, the high one becomes U+FFFD and parsing
								// resumes AT that escape, so it is decoded on its own (e.g.
								// "\uD800A" -> U+FFFD 'A'). Previously the second escape was
								// consumed and silently lost.
								const size_t secondEscape = p;
								p += 2;
								unsigned lo;
								if (!hex4 (lo))
									return false;
								if (lo >= 0xDC00 && lo <= 0xDFFF)
									cp = 0x10000 + ((cp - 0xD800) << 10) + (lo - 0xDC00);
								else
								{
									cp = 0xFFFD;
									p = secondEscape; // re-read the non-low escape next iteration
								}
							}
							else
								cp = 0xFFFD;
						}
						else if (cp >= 0xDC00 && cp <= 0xDFFF)
							cp = 0xFFFD; // low surrogate without a preceding high one
						appendUtf8 (out, cp);
						break;
					}
					default: return fail ("bad escape");
				}
			}
			else
				out += c;
		}
		return fail ("unterminated string");
	}

	/// Parses one value of any type at p (leading whitespace skipped) into `out`.
	/// Recursive for arrays/objects; depth counts nesting so deeply nested input fails
	/// cleanly instead of overflowing the stack. `depth` is only decremented on success
	/// (after a failure the parse is abandoned anyway). Trailing whitespace is left to the
	/// caller.
	bool value (Value& out)
	{
		if (++depth > 200)
			return fail ("nesting too deep");
		ws ();
		if (p >= s.size ())
			return fail ("unexpected end");
		char c = s[p];
		bool ok = true;
		if (c == '{')
		{
			// Object: '{' [ string ':' value { ',' string ':' value } ] '}'.
			// A trailing comma ("{"a":1,}") is rejected: after ',' a string key is required.
			++p;
			out = Value::object ();
			ws ();
			if (p < s.size () && s[p] == '}')
				++p;
			else
			{
				for (;;)
				{
					ws ();
					std::string key;
					if (!str (key))
						return false;
					ws ();
					if (p >= s.size () || s[p] != ':')
						return fail ("expected ':'");
					++p;
					Value v;
					if (!value (v))
						return false;
					out.set (key, std::move (v)); // duplicate key: last one wins
					ws ();
					if (p < s.size () && s[p] == ',')
					{
						++p;
						continue;
					}
					if (p < s.size () && s[p] == '}')
					{
						++p;
						break;
					}
					return fail ("expected ',' or '}'");
				}
			}
		}
		else if (c == '[')
		{
			// Array: '[' [ value { ',' value } ] ']'. A trailing comma is rejected because
			// value() then fails on ']'.
			++p;
			out = Value::array ();
			ws ();
			if (p < s.size () && s[p] == ']')
				++p;
			else
			{
				for (;;)
				{
					Value v;
					if (!value (v))
						return false;
					out.push (std::move (v));
					ws ();
					if (p < s.size () && s[p] == ',')
					{
						++p;
						continue;
					}
					if (p < s.size () && s[p] == ']')
					{
						++p;
						break;
					}
					return fail ("expected ',' or ']'");
				}
			}
		}
		else if (c == '"')
		{
			std::string st;
			ok = str (st);
			out = Value (std::move (st));
		}
		// Literals. compare() clamps to the end of s, so a truncated "tru" simply does not
		// match. The character after the literal is not checked here ("truex"); the
		// caller then fails on it as an unexpected/trailing character.
		else if (s.compare (p, 4, "true") == 0)
		{
			p += 4;
			out = Value (true);
		}
		else if (s.compare (p, 5, "false") == 0)
		{
			p += 5;
			out = Value (false);
		}
		else if (s.compare (p, 4, "null") == 0)
		{
			p += 4;
			out = Value ();
		}
		else if (c == '-' || (c >= '0' && c <= '9'))
		{
			// Number: take the longest run of characters that can appear in a JSON number,
			// then let std::from_chars convert it and require that it consumed the whole run
			// ("1.2.3" or "1e" fail). Lenient compared to RFC 8259: leading zeros ("01")
			// and a bare trailing dot ("1.") are accepted. Hex/inf/nan forms cannot occur
			// because only digits, '.', 'e', 'E', '+' and '-' are collected.
			// std::from_chars is used instead of strtod because it is locale-independent:
			// strtod uses the C locale's decimal separator, so if the host process switched
			// LC_NUMERIC to a comma locale, "0.5" failed to parse. from_chars always expects
			// '.'. A value outside the double range (e.g. "1e999") is now rejected as
			// "number out of range" instead of silently becoming infinity.
			size_t start = p;
			if (s[p] == '-')
				++p;
			while (p < s.size () &&
			       ((s[p] >= '0' && s[p] <= '9') || s[p] == '.' || s[p] == 'e' || s[p] == 'E' ||
			        s[p] == '+' || s[p] == '-'))
				++p;
			const char* first = s.data () + start;
			const char* last = s.data () + p;
			double d = 0;
			auto r = std::from_chars (first, last, d); // chars_format::general (fixed or exponent)
			if (r.ec == std::errc::result_out_of_range)
				return fail ("number out of range");
			if (r.ec != std::errc () || r.ptr != last)
				return fail ("bad number");
			out = Value (d);
		}
		else
			return fail ("unexpected character");
		--depth;
		return ok;
	}
};

} // namespace

//------------------------------------------------------------------------------
/// Parses exactly one value and then requires that only whitespace remains, so
/// concatenated or truncated-then-garbage input is rejected as a whole.
bool Value::parse (const std::string& text, Value& out, std::string& error)
{
	Parser parser (text);
	Value v;
	if (!parser.value (v))
	{
		error = parser.error;
		return false;
	}
	parser.ws ();
	if (parser.p != text.size ())
	{
		error = "trailing characters at offset " + std::to_string (parser.p);
		return false;
	}
	out = std::move (v);
	return true;
}

} // namespace Json
} // namespace CsoundVisual
