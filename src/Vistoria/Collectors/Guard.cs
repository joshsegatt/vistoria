using System;
using System.ComponentModel;
using System.Management;

namespace Vistoria.Collectors
{
    static class Guard
    {
        public const string DeniedMessage = "Acesso negado: rode como administrador";

        // One failing source must not sink the others: record why, and the rules report it as "not verified".
        public static void Try(Evidence ev, string key, Action a)
        {
            try { a(); }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                ev.Errors[key] = IsDenied(e) ? DeniedMessage : e.GetType().Name + ": " + e.Message.Trim();
            }
        }

        // Checked by code, not message text: Windows localizes the messages.
        public static bool IsDenied(Exception e) =>
            e is UnauthorizedAccessException
            || (e is ManagementException m && m.ErrorCode == ManagementStatus.AccessDenied)
            || (e is Win32Exception w && w.NativeErrorCode == 5)
            || e.HResult == unchecked((int)0x80070005);
    }
}
