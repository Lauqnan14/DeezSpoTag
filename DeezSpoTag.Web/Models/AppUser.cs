using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Identity;

namespace DeezSpoTag.Web.Models;

[SuppressMessage("Major Code Smell", "S2094:Classes should not be empty", Justification = "Identity requires an application user type even when no custom fields are defined yet.")]
public sealed class AppUser : IdentityUser
{
}
