using System.Windows;

namespace IfcPropertyEditor
{
    public partial class EditPropertyWindow : Window
    {
        public string NewValue { get; private set; } = "";

        public EditPropertyWindow(string propertyName, string currentValue)
        {
            InitializeComponent();

            PropertyNameText.Text = propertyName;
            ValueTextBox.Text = currentValue;
            ValueTextBox.SelectAll();
            ValueTextBox.Focus();
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            NewValue = ValueTextBox.Text;
            DialogResult = true;

        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}