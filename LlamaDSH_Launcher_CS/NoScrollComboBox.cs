using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LlamaDSH_Launcher;

public class NoScrollComboBox : ComboBox
{
	static NoScrollComboBox()
	{
		EventManager.RegisterClassHandler(typeof(NoScrollComboBox), UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnPreviewMouseWheel), handledEventsToo: true);
	}

	private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
	{
		e.Handled = true;
	}
}
