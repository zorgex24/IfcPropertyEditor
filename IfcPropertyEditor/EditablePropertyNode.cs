namespace IfcPropertyEditor
{
    public class EditablePropertyNode
    {
        public string Name { get; set; } = "";
        public object SourceObject { get; set; } = null!;
        public string SourcePropertyName { get; set; } = "";
        public string CurrentValue { get; set; } = "";
        public bool IsHeaderProperty { get; set; }
    }
}