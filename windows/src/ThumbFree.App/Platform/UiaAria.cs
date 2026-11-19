using System.Runtime.InteropServices;

namespace ThumbFree.App.Platform;

/// <summary>
/// An element's ARIA properties ("multiline=true;…") through UI Automation's COM API. The managed
/// System.Windows.Automation client predates the property, and browsers report text areas and rich editors (Gmail's
/// compose box) only there.
/// </summary>
internal static class UiaAria
{
    private const int RuntimeIdPropertyId = 30000;
    private const int AriaPropertiesPropertyId = 30102;
    private const int TreeScopeSubtree = 7;

    /// <summary>
    /// The ARIA properties of the element of <paramref name="window"/> with <paramref name="runtimeId"/>: read from the
    /// focused element when it's that one, else looked up in the window (Chrome 157 reports the focus as the browser's
    /// pane, see <see cref="ChromiumAccessibility.FocusedInPage"/>). The focused element's when the id is unknown. Null
    /// when the element or the property isn't available.
    /// </summary>
    public static string? AriaProperties(nint window, int[]? runtimeId)
    {
        IUIAutomation? automation = null;
        var created = new List<object>();
        try
        {
            automation = (IUIAutomation)new CUIAutomation();
            var target = automation.GetFocusedElement();
            if (target is not null)
            {
                created.Add(target);
            }

            if (runtimeId is not null && (target is null || target.GetRuntimeId() is not { } focusedId || !focusedId.AsSpan().SequenceEqual(runtimeId)))
            {
                target = null;
                if (window != 0 && automation.ElementFromHandle(window) is { } root)
                {
                    created.Add(root);
                    var condition = automation.CreatePropertyCondition(RuntimeIdPropertyId, runtimeId);
                    created.Add(condition);
                    target = root.FindFirst(TreeScopeSubtree, condition);
                    if (target is not null)
                    {
                        created.Add(target);
                    }
                }
            }

            return target?.GetCurrentPropertyValue(AriaPropertiesPropertyId) as string;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidComObjectException or ArgumentException or TimeoutException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            foreach (var item in created)
            {
                if (Marshal.IsComObject(item))
                {
                    Marshal.ReleaseComObject(item);
                }
            }

            if (automation is not null)
            {
                Marshal.ReleaseComObject(automation);
            }
        }
    }

    [ComImport]
    [Guid("ff48dba4-60ef-4201-aa87-54103eef594e")]
    private class CUIAutomation
    {
    }

    /// <summary>The slots up to CreatePropertyCondition, in order; only the typed ones are called.</summary>
    [ComImport]
    [Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomation
    {
        void CompareElements();

        void CompareRuntimeIds();

        void GetRootElement();

        IUIAutomationElement? ElementFromHandle(nint window);

        void ElementFromPoint();

        IUIAutomationElement? GetFocusedElement();

        void GetRootElementBuildCache();

        void ElementFromHandleBuildCache();

        void ElementFromPointBuildCache();

        void GetFocusedElementBuildCache();

        void CreateTreeWalker();

        void GetControlViewWalker();

        void GetContentViewWalker();

        void GetRawViewWalker();

        void GetRawViewCondition();

        void GetControlViewCondition();

        void GetContentViewCondition();

        void CreateCacheRequest();

        void CreateTrueCondition();

        void CreateFalseCondition();

        IUIAutomationCondition CreatePropertyCondition(int propertyId, [MarshalAs(UnmanagedType.Struct)] object value);
    }

    [ComImport]
    [Guid("352ffba8-0973-437c-a61f-f64cafd81df9")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationCondition
    {
    }

    /// <summary>The slots up to GetCurrentPropertyValue, in order; only the typed ones are called.</summary>
    [ComImport]
    [Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElement
    {
        void SetFocus();

        [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_I4)]
        int[]? GetRuntimeId();

        IUIAutomationElement? FindFirst(int scope, IUIAutomationCondition condition);

        void FindAll();

        void FindFirstBuildCache();

        void FindAllBuildCache();

        void BuildUpdatedCache();

        [return: MarshalAs(UnmanagedType.Struct)]
        object? GetCurrentPropertyValue(int propertyId);
    }
}
