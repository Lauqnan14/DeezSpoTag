using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.AspNetCore.Identity;

namespace DeezSpoTag.Web.Models;

public sealed class AppUser : IdentityUser
{
    [NotMapped]
    public string EffectiveLogin => NormalizedEmail ?? NormalizedUserName ?? Id;
}
