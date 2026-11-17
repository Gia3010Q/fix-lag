using System;

namespace RBX_Alt_Manager
{
    internal static class ApiAuthentication
    {
        internal static bool ValidPassword(string configured, string supplied)
        {
            return !string.IsNullOrEmpty(configured) && configured.Length >= 6 &&
                !string.IsNullOrEmpty(supplied) &&
                string.Equals(configured, supplied, StringComparison.Ordinal);
        }
    }
}
