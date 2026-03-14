namespace DeezSpoTag.Core.Models;

/// <summary>
/// Genre model for music categorization
/// </summary>
public class Genre
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Picture { get; set; }

    public Genre()
    {
    }

    public Genre(int id, string name)
    {
        Id = id;
        Name = name;
    }

    public Genre(string name)
    {
        Name = name;
    }

    public override string ToString()
    {
        return Name;
    }

    public override bool Equals(object? obj)
    {
        if (obj is null || obj.GetType() != typeof(Genre))
        {
            return false;
        }

        var other = (Genre)obj;
        return Id == other.Id && Name == other.Name;
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Id, Name);
    }
}
