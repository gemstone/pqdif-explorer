//******************************************************************************************************
//  ElementExtensions.cs - Gbtc
//
//  Copyright © 2024, Grid Protection Alliance.  All Rights Reserved.
//
//  Licensed to the Grid Protection Alliance (GPA) under one or more contributor license agreements. See
//  the NOTICE file distributed with this work for additional information regarding copyright ownership.
//  The GPA licenses this file to you under the MIT License (MIT), the "License"; you may not use this
//  file except in compliance with the License. You may obtain a copy of the License at:
//
//      http://opensource.org/licenses/MIT
//
//  Unless agreed to in writing, the subject software distributed under the License is distributed on an
//  "AS-IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. Refer to the
//  License for the specific language governing permissions and limitations.
//
//  Code Modification History:
//  ----------------------------------------------------------------------------------------------------
//  06/23/2020 - Stephen C. Wills
//       Generated original version of source code.
//
//******************************************************************************************************

using System.Numerics;
using System.Text;
using Gemstone.Numeric.ComplexExtensions;
using Gemstone.PQDIF;
using Gemstone.PQDIF.Physical;

namespace PQDIFExplorer.Web;

public static class ElementExtensions
{
    // Sets the value of the element to the given value.
    public static void SetValue(this Element element, string value)
    {
        if (element.TypeOfElement == ElementType.Scalar)
            SetValue((ScalarElement)element, value);
        else if (element.TypeOfElement == ElementType.Vector)
            SetValue((VectorElement)element, value);
    }

    // Sets the value of the scalar element to the given value.
    public static void SetValue(this ScalarElement element, string value)
    {
        string trim = value.Trim();
        Tag? tag = Tag.GetTag(element.TagOfElement);

        // Determine whether the tag definition contains
        // a list of identifiers which can be used to
        // display the value in a more readable format
        IReadOnlyCollection<Identifier> identifiers = tag?.ValidIdentifiers ?? Array.Empty<Identifier>();

        // Some identifier collections define a set of bitfields which can be
        // combined to represent a collection of states rather than a single value
        // and these are identified by the values being represented in hexadecimal
        List<Identifier> bitFields = identifiers.Where(id => id.Value.StartsWith("0x")).ToList();

        if (bitFields.Count > 0)
        {
            HashSet<string> activeBitFields;
            uint bitSet = 0u;

            trim = value.Trim('{', '}');
            activeBitFields = new HashSet<string>(trim.Split(',').Select(bitField => bitField.Trim()), StringComparer.OrdinalIgnoreCase);

            foreach (Identifier bitField in bitFields)
            {
                if (activeBitFields.Contains(bitField.Name))
                    bitSet |= Convert.ToUInt32(bitField.Value, 16);
            }

            trim = bitSet.ToString();
        }
        else if (identifiers.Count > 0)
        {
            foreach (Identifier identifier in identifiers)
            {
                if (identifier.Name == value)
                    trim = identifier.Value;
            }
        }

        if (element.TypeOfValue == PhysicalType.Guid)
            element.SetGuid(Guid.Parse(trim));
        else if (element.TypeOfValue == PhysicalType.Complex8)
            element.SetComplex8(trim.FromComplexNotation());
        else if (element.TypeOfValue == PhysicalType.Complex16)
            element.SetComplex16(trim.FromComplexNotation());
        else
            element.Set(trim);
    }

    // Sets the value of the vector element to the given value.
    public static void SetValue(this VectorElement element, string value)
    {
        string[] values = value.Trim('{', '}').Split(',');

        if (element.TypeOfValue == PhysicalType.Char1)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(value + (char)0);
            element.Size = bytes.Length;
            element.SetValues(bytes, 0);
        }
        else if (element.TypeOfValue == PhysicalType.Char2)
        {
            byte[] bytes = Encoding.Unicode.GetBytes(value + (char)0);
            element.Size = bytes.Length;
            element.SetValues(bytes, 0);
        }
        else
        {
            element.Size = values.Length;

            for (int i = 0; i < values.Length; i++)
            {
                string trim = values[i].Trim();

                if (element.TypeOfValue == PhysicalType.Guid)
                    element.SetGuid(i, Guid.Parse(trim));
                else if (element.TypeOfValue == PhysicalType.Complex8)
                    element.Set(i, trim.FromComplexNotation());
                else if (element.TypeOfValue == PhysicalType.Complex16)
                    element.Set(i, trim.FromComplexNotation());
                else
                    element.Set(i, trim);
            }
        }
    }

    // Converts the value of the element to a string representation.
    public static string? ValueAsString(this Element element)
    {
        if (element.TypeOfElement == ElementType.Scalar)
            return ValueAsString((ScalarElement)element);

        if (element.TypeOfElement == ElementType.Vector)
            return ValueAsString((VectorElement)element);

        if (element is ErrorElement errorElement)
            return ValueAsString(errorElement);

        return null;
    }

    // Converts the value of the element to a string representation.
    public static string ValueAsString(this ScalarElement element)
    {
        // Get the value of the element
        // parsed from the PQDIF file
        object value = element.Get();

        // Get the tag definition for the element being displayed
        Tag? tag = Tag.GetTag(element.TagOfElement);

        // Use the format string specified by the tag
        // or a default format string if not specified
        string valueString = element.TypeOfValue switch
        {
            PhysicalType.Complex8 => ((Complex)value).ToComplexNotation(),
            PhysicalType.Complex16 => ((Complex)value).ToComplexNotation(),
            PhysicalType.Timestamp => string.Format(tag?.FormatString ?? "{0:yyyy-MM-dd HH:mm:ss.fffffff}", value),
            _ => string.Format(tag?.FormatString ?? "{0}", value)
        };

        // Determine whether the tag definition contains
        // a list of identifiers which can be used to
        // display the value in a more readable format
        IEnumerable<Identifier> identifiers = tag?.ValidIdentifiers
            ?? Enumerable.Empty<Identifier>();

        // Some identifier collections define a set of bitfields which can be
        // combined to represent a collection of states rather than a single value
        // and these are identified by the values being represented in hexadecimal
        List<Identifier> bitFields = identifiers.Where(id => id.Value.StartsWith("0x")).ToList();

        if (bitFields.Count > 0)
        {
            // If the value is not convertible,
            // it cannot be converted to an
            // integer to check for bit states
            if (value is not IConvertible)
                return valueString;

            // Convert the value to an integer which can
            // then be checked for the state of its bits
            uint bitSet = Convert.ToUInt32(value);

            // Get the names of the bitfields in the
            // collection of bitfields that are set
            List<string> setBits = bitFields
                .Select(id => new { id.Name, Value = Convert.ToUInt32(id.Value, 16) })
                .Where(id => bitSet == id.Value || (bitSet & id.Value) > 0u)
                .Select(id => id.Name)
                .ToList();

            // If none of the bitfields are set,
            // show just the value by itself
            if (setBits.Count == 0)
                return valueString;

            // If any of the bitfields are set,
            // display them as a comma-separated
            // list alongside the value
            string join = string.Join(", ", setBits);
            return $"{{ {join} }} ({valueString})";
        }

        // Determine if there are any identifiers whose value exactly
        // matches the string representation of the element's value
        string? identifierName = identifiers.SingleOrDefault(id => id.Value == valueString)?.Name;

        if (identifierName is not null)
            return $"{identifierName} ({element.Get()})";

        // If the tag could not be recognized as
        // one that can be displayed in a more
        // readable form, display the value by itself
        return valueString;
    }

    // Converts the value of the element to a string representation.
    public static string ValueAsString(this VectorElement element)
    {
        // The physical types Char1 and Char2 indicate the value is a string
        if (element.TypeOfValue == PhysicalType.Char1)
            return Encoding.ASCII.GetString(element.GetValues()).Trim((char)0);

        if (element.TypeOfValue == PhysicalType.Char2)
            return Encoding.Unicode.GetString(element.GetValues()).Trim((char)0);

        // Get the tag definition of the element being displayed
        Tag? tag = Tag.GetTag(element.TagOfElement);

        // Determine the format in which to display the values
        // based on the tag definition and the type of the value
        Func<int, string> formatter = element.TypeOfValue switch
        {
            PhysicalType.Complex8 => index => ((Complex)element.Get(index)).ToComplexNotation(),
            PhysicalType.Complex16 => index => ((Complex)element.Get(index)).ToComplexNotation(),

            PhysicalType.Timestamp => index =>
            {
                string format = tag?.FormatString ?? "{0:yyyy-MM-dd HH:mm:ss.fffffff}";
                object value = element.Get(index);
                return string.Format(format, value);
            }
            ,

            _ => index =>
            {
                string format = tag?.FormatString ?? "{0}";
                object value = element.Get(index);
                return string.Format(format, value);
            }
        };

        // Convert the values to their string representations
        IEnumerable<string> values = Enumerable
            .Range(0, element.Size)
            .Select(formatter);

        // Join the values in the collection
        // to a single, comma-separated string
        string join = string.Join(", ", values);

        // Wrap the string in curly braces and return
        return $"{{ {join} }}";
    }

    // Converts the value of the element to a string representation.
    public static string ValueAsString(this ErrorElement element)
    {
        return element.Exception.Message;
    }

    // Converts the value of the element to a hexadecimal representation.
    public static string? ValueAsHex(this Element element)
    {
        if (element.TypeOfElement == ElementType.Scalar)
            return ValueAsHex((ScalarElement)element);

        if (element.TypeOfElement == ElementType.Vector)
            return ValueAsHex((VectorElement)element);

        return null;
    }

    // Converts the value of the element to a hexadecimal representation.
    public static string ValueAsHex(this ScalarElement element)
    {
        byte[] value = element.GetValue();

        string hex = BitConverter
            .ToString(value)
            .Replace("-", "");

        return $"0x{hex}";
    }

    // Converts the value of the element to a hexadecimal representation.
    public static string ValueAsHex(this VectorElement element)
    {
        byte[] values = element.GetValues();

        string hex = BitConverter
            .ToString(values)
            .Replace("-", "");

        return $"0x{hex}";
    }
}
