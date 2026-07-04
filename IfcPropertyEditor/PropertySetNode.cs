using Xbim.Ifc2x3.Kernel;
using Xbim.Ifc2x3.PropertyResource;

namespace IfcPropertyEditor
{
    public class PropertySetNode
    {
        public IfcRelDefinesByProperties Relation { get; set; } = null!;
        public IfcPropertySet PropertySet { get; set; } = null!;
        public IfcObject OwnerEntity { get; set; } = null!;
    }
}