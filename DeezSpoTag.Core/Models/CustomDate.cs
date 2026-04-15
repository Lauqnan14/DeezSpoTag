using System.Globalization;

namespace DeezSpoTag.Core.Models;

/// <summary>
/// Custom date model (ported from deezspotag CustomDate.ts)
/// </summary>
public class CustomDate
{
    public string Day { get; set; } = "00";
    public string Month { get; set; } = "00";
    public string Year { get; set; } = "0000";

    public CustomDate()
    {
    }

    public CustomDate(string day, string month, string year)
    {
        Day = day;
        Month = month;
        Year = year;
    }

    /// <summary>
    /// Fix day and month to ensure they are 2 digits (ported from deezspotag fixDayMonth)
    /// </summary>
    public void FixDayMonth()
    {
        if (Day.Length == 1) Day = "0" + Day;
        if (Month.Length == 1) Month = "0" + Month;
    }

    /// <summary>
    /// Format date according to specified format (ported from deezspotag format method)
    /// </summary>
    public string Format(string format)
    {
        return format.ToLower() switch
        {
            "y" => Year,
            "m" => Month,
            "d" => Day,
            "ym" => $"{Year}-{Month}",
            "yd" => $"{Year}-{Day}",
            "md" => $"{Month}-{Day}",
            "ymd" => $"{Year}-{Month}-{Day}",
            "dmy" => $"{Day}-{Month}-{Year}",
            "mdy" => $"{Month}-{Day}-{Year}",
            _ => $"{Year}-{Month}-{Day}" // Default format
        };
    }

    /// <summary>
    /// Check if date is valid
    /// </summary>
    public bool IsValid()
    {
        if (!int.TryParse(Year, out var year) || year < 1900 || year > DateTime.Now.Year + 10)
            return false;

        if (!int.TryParse(Month, out var month) || month < 1 || month > 12)
            return false;

        if (!int.TryParse(Day, out var day) || day < 1 || day > 31)
            return false;

        // Check if the day is valid for the month
        try
        {
            _ = new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Unspecified);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Convert to DateTime if valid
    /// </summary>
    public DateTime? ToDateTime()
    {
        if (!IsValid()) return null;

        try
        {
            return new DateTime(
                int.Parse(Year, CultureInfo.InvariantCulture),
                int.Parse(Month, CultureInfo.InvariantCulture),
                int.Parse(Day, CultureInfo.InvariantCulture),
                0,
                0,
                0,
                DateTimeKind.Unspecified);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Create CustomDate from DateTime
    /// </summary>
    public static CustomDate FromDateTime(DateTime dateTime)
    {
        return new CustomDate(
            dateTime.Day.ToString("D2"),
            dateTime.Month.ToString("D2"),
            dateTime.Year.ToString()
        );
    }

    /// <summary>
    /// Create CustomDate from date string (YYYY-MM-DD format)
    /// </summary>
    public static CustomDate FromString(string dateString)
    {
        if (string.IsNullOrEmpty(dateString))
            return new CustomDate();

        try
        {
            if (DateTime.TryParse(dateString, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal, out var date))
            {
                return FromDateTime(date);
            }

            // Try to parse YYYY-MM-DD format manually
            var parts = dateString.Split('-');
            if (parts.Length >= 3)
            {
                return new CustomDate(parts[2], parts[1], parts[0]);
            }
            else if (parts.Length == 2)
            {
                return new CustomDate("00", parts[1], parts[0]);
            }
            else if (parts.Length == 1)
            {
                return new CustomDate("00", "00", parts[0]);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // If parsing fails, return empty date
        }

        return new CustomDate();
    }

    public override string ToString()
    {
        return Format("ymd");
    }

    public override bool Equals(object? obj)
    {
        if (obj is null || obj.GetType() != typeof(CustomDate))
        {
            return false;
        }

        var other = (CustomDate)obj;
        return Day == other.Day && Month == other.Month && Year == other.Year;
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Day, Month, Year);
    }
}
