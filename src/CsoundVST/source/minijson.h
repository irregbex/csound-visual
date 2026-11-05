//------------------------------------------------------------------------------
// Csound Visual VST3 - minimal JSON value, parser and serializer
//
// Just enough JSON for the IPC protocol: null, bool, number (double), string
// (UTF-8, full escape support including \uXXXX surrogate pairs), array and
// object (insertion order preserved). Written in-house so the plug-in has no
// third-party dependency besides the VST3 SDK.
//
// Used by plugin.cpp to parse IPC requests and build IPC replies / descriptor
// files; never used on the audio thread (it allocates freely).
//
// Design choices:
//   * One tagged value type with all payload members present (no variant/union):
//     simple and safe, at the cost of some memory per value - irrelevant for
//     messages of a few KB.
//   * Objects are a vector of (key, value) pairs: insertion order is kept for
//     readable output and lookups are linear, fine for objects with a handful
//     of keys. Duplicate keys in parsed input: the last one wins.
//   * Numbers are always double (integers are exact up to 2^53).
//   * Lookups of missing keys / wrong types return a shared null value or the
//     caller's fallback instead of throwing, so request handlers can read
//     optional fields without checks.
//------------------------------------------------------------------------------
#pragma once

#include <map>
#include <memory>
#include <string>
#include <utility>
#include <vector>

namespace CsoundVisual {
namespace Json {

/// A JSON value: null, bool, number, string, array or object. Regular value type
/// (copyable, movable); arrays and objects own their children.
class Value
{
public:
	/// JSON type of a value.
	enum class Type { Null, Bool, Number, String, Array, Object };

	/// Implicit converting constructors, so `obj.set("ok", true)`, `set("sr", 48000.0)` or
	/// `set("name", str)` just work. One overload per integer type avoids ambiguous
	/// conversions (e.g. unsigned long vs double); all integers are stored as double.
	Value () = default;            ///< null
	Value (std::nullptr_t) {}      ///< null
	Value (bool b) : type (Type::Bool), boolean (b) {}
	Value (int v) : type (Type::Number), number (v) {}
	Value (long long v) : type (Type::Number), number ((double)v) {}
	Value (unsigned v) : type (Type::Number), number (v) {}
	Value (unsigned long long v) : type (Type::Number), number ((double)v) {}
	Value (unsigned long v) : type (Type::Number), number ((double)v) {}
	Value (long v) : type (Type::Number), number ((double)v) {}
	Value (double v) : type (Type::Number), number (v) {}
	/// String from UTF-8 text. Must not be null. Without this overload a string literal
	/// would convert to bool (pointer -> bool beats pointer -> std::string).
	Value (const char* s) : type (Type::String), string (s) {}
	Value (std::string s) : type (Type::String), string (std::move (s)) {}

	/// Empty array / empty object (a default Value is null, which serialises as "null").
	static Value array () { Value v; v.type = Type::Array; return v; }
	static Value object () { Value v; v.type = Type::Object; return v; }

	Type getType () const { return type; }
	bool isNull () const { return type == Type::Null; }
	bool isString () const { return type == Type::String; }
	bool isNumber () const { return type == Type::Number; }
	bool isObject () const { return type == Type::Object; }
	bool isArray () const { return type == Type::Array; }

	/// Typed accessors with a fallback when the type does not match.
	std::string asString (const std::string& fallback = {}) const
	{
		return type == Type::String ? string : fallback;
	}
	double asNumber (double fallback = 0) const { return type == Type::Number ? number : fallback; }
	bool asBool (bool fallback = false) const { return type == Type::Bool ? boolean : fallback; }

	/// Array append (turns a null value into an array).
	/// Note: any non-array value (not only null) is replaced by an empty array first.
	/// @return reference to the stored element (invalidated by the next push).
	Value& push (Value v);
	/// Object member set (turns a null value into an object); replaces an existing key.
	/// Note: any non-object value is replaced by an empty object first. Linear key search.
	/// @return reference to the stored member value (invalidated by the next new key).
	Value& set (const std::string& key, Value v);
	/// Object member lookup; returns a shared null value when missing.
	/// Also returns null when this value is not an object. Never inserts (const).
	const Value& operator[] (const std::string& key) const;
	/// Array element access (no bounds check beyond returning null).
	/// Out of range or not an array -> shared null value.
	const Value& at (size_t i) const;
	/// Element count for arrays, member count for objects (0 for scalars).
	size_t size () const { return type == Type::Array ? items.size () : members.size (); }
	/// True if this is an object that contains `key`.
	bool has (const std::string& key) const;

	/// Compact serialization (UTF-8, control characters escaped).
	/// No whitespace. Non-finite numbers are written as null (JSON has no NaN/Inf);
	/// integral numbers below 1e15 are written without a decimal point, others with 17
	/// significant digits (round-trips any double).
	std::string dump () const;

	/// Parses UTF-8 JSON text. On error returns false and fills error.
	/// The whole text must be one value (surrounding whitespace allowed). `out` is only
	/// assigned on success. `error` reads like "expected ':' at offset 42".
	static bool parse (const std::string& text, Value& out, std::string& error);

private:
	/// Appends the serialisation of this value to `out` (recursive helper of dump()).
	void dumpTo (std::string& out) const;

	Type type = Type::Null;  ///< which of the payload members below is meaningful
	bool boolean = false;    ///< payload for Bool
	double number = 0;       ///< payload for Number
	std::string string;      ///< payload for String (UTF-8)
	std::vector<Value> items; ///< payload for Array
	std::vector<std::pair<std::string, Value>> members; ///< payload for Object, in insertion order
};

/// Escapes a string for inclusion in JSON (with quotes).
/// Escapes '"', '\\', the short forms \b \f \n \r \t and other control characters below
/// 0x20 as \u00XX. Bytes >= 0x80 (UTF-8) and '/' are copied unchanged; the input is not
/// validated as UTF-8.
std::string quote (const std::string& s);

} // namespace Json
} // namespace CsoundVisual
