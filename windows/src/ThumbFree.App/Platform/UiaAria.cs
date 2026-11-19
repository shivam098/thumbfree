using System.Runtime.InteropServices;

namespace ThumbFree.App.Platform;

/// <summary>
/// The focused element's ARIA properties ("multiline=true;…") through UI Automation's COM API. The managed
/// System.Windows.Automation client predates the property, and browsers report text areas and rich editors (Gmail's
/// compose box) only there.
/// </summary>
internal static class UiaAria
{
    private const int AriaPropertiesPropertyId = 30102;

    /// <summary>Null when the element or the property isn't available.</summary>
    public static string? FocusedAriaProperties()
    {
        try
        {
            var automation = (IUIAutomation)new CUIAutomation();
            var focused = automation.GetFocusedElement();
            try
            {
                return focused?.GetCurrentPropertyValue(AriaPropertiesPropertyId) as string;
            }
            finally
            {
                if (focused is not null)
                {
                    Marshal.ReleaseComObject(focused);
                }

                Marshal.ReleaseComObject(automation);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidComObjectException)
        {
            return null;
        }
    }

    [ComImport]
    [Guid("ff48dba4-60ef-4201-aa87-54103eef594e")]
    private class CUIAutomation
    {
    }

    /// <summary>Only the slots up to GetFocusedElement; the others are never called.</summary>
    [ComImport]
    [Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomation
    {
        void CompareElements();

        void CompareRuntimeIds();

        void GetRootElement();

        void ElementFromHandle();

        void ElementFromPoint();

        IUIAutomationElement? GetFocusedElement();
    }

    /// <summary>Only the slots up to GetCurrentPropertyValue; the others are never called.</summary>
    [ComImport]
    [Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElement
    {
        void SetFocus();

        void GetRuntimeId();

        void FindFirst();

        void FindAll();

        void FindFirstBuildCache();

        void FindAllBuildCache();

        void BuildUpdatedCache();

        [return: MarshalAs(UnmanagedType.Struct)]
        object? GetCurrentPropertyValue(int propertyId);
    }
}
