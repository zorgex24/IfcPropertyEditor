using Microsoft.Win32;
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Xbim.Ifc;
using Xbim.Ifc2x3.Kernel;
using System.Collections.Generic;
using Xbim.Ifc2x3.ProductExtension;
using Xbim.Ifc2x3.PropertyResource;
using Xbim.Ifc2x3.MeasureResource;
using System.Collections;
using Xbim.Ifc2x3.QuantityResource;

namespace IfcPropertyEditor
{
    public partial class MainWindow : Window
    {
        private IfcStore? _model;
        private string? _currentFilePath;
        private IfcRoot? _selectedEntity;
        private bool _hasUnsavedChanges = false;


        public MainWindow()
        {
            InitializeComponent();
        }

        private void OpenIfc_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "IFC files (*.ifc)|*.ifc|All files (*.*)|*.*"
            };

            if (dialog.ShowDialog() != true)
                return;

            _currentFilePath = dialog.FileName;

            try
            {
                _model = IfcStore.Open(_currentFilePath);

                LoadEntitiesTree();

                MessageBox.Show("IFC-файл успешно открыт.");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Ошибка открытия IFC-файла:\n" + ex.Message);
            }
        }

        private void LoadEntitiesTree()
        {
            EntitiesTree.Items.Clear();
            PropertiesTree.Items.Clear();

            var fileInfoNode = new TreeViewItem
            {
                Header = "File Header",
                Tag = new IfcFileInfoNode()
            };

            EntitiesTree.Items.Add(fileInfoNode);

            if (_model == null)
                return;

            var projects = _model.Instances
                .OfType<IfcProject>()
                .ToList();

            foreach (var project in projects)
            {
                var projectNode = CreateEntityNode(project);
                EntitiesTree.Items.Add(projectNode);

                AddChildrenRecursive(projectNode, project);
            }
        }

        private void EntitiesTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            PropertiesTree.Items.Clear();

            if (EntitiesTree.SelectedItem is not TreeViewItem item)
                return;

            if (item.Tag is IfcFileInfoNode)
            {
                ShowFileHeaderProperties();
                return;
            }

            if (item.Tag is not IfcRoot entity)
                return;

            ShowEntityPropertiesGrouped(entity);
        }

        private void SaveIfc_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("Сохранение добавим позже.");
        }

        private TreeViewItem CreatePropertyNode(string name, string value)
        {
            return new TreeViewItem
            {
                Header = CreatePropertyHeader(name, value)
            };
        }

        private void AddChildrenRecursive(TreeViewItem parentNode, IfcRoot parentEntity)
        {
            var children = new List<IfcRoot>();

            // 1. Пространственная структура: Project -> Site -> Building -> Storey
            if (parentEntity is IfcObjectDefinition objectDefinition)
            {
                var decomposedChildren = objectDefinition.IsDecomposedBy
                    .SelectMany(rel => rel.RelatedObjects)
                    .OfType<IfcRoot>();

                children.AddRange(decomposedChildren);
            }

            // 2. Элементы внутри этажа/контейнера: Storey -> Walls / Slabs / Proxies
            if (parentEntity is IfcSpatialStructureElement spatialElement)
            {
                var containedElements = spatialElement.ContainsElements
                    .SelectMany(rel => rel.RelatedElements)
                    .OfType<IfcRoot>();

                children.AddRange(containedElements);
            }

            foreach (var child in children
                .Distinct()
                .OrderBy(x => x.GetType().Name)
                .ThenBy(x => x.Name?.Value?.ToString() ?? ""))
            {
                var childNode = CreateEntityNode(child);
                parentNode.Items.Add(childNode);

                AddChildrenRecursive(childNode, child);
            }
        }

        private void ShowFileHeaderProperties()
        {
            if (_model == null)
                return;

            var properties = new List<IfcPropertyItem>();

            AddObjectProperties(properties, "FileDescription", _model.Header.FileDescription);
            AddObjectProperties(properties, "FileName", _model.Header.FileName);
            AddObjectProperties(properties, "FileSchema", _model.Header.FileSchema);

            properties.Insert(0, new IfcPropertyItem
            {
                Name = "File path",
                Value = _currentFilePath ?? ""
            });

            PropertiesTree.Items.Clear();

            foreach (var property in properties.OrderBy(x => x.Name))
            {
                var node = CreatePropertyNode(property.Name, property.Value);

                // Разрешаем редактировать только FileDescription.Description.
                // FileName.OriginatingSystem отдельно не редактируем:
                // он синхронизируется из ApplicationFullName + Version.
                if (property.Name == "FileDescription.Description" &&
                    TryGetHeaderEditTarget(property.Name, out var sourceObject, out var sourcePropertyName))
                {
                    node.Tag = new EditablePropertyNode
                    {
                        Name = property.Name,
                        SourceObject = sourceObject,
                        SourcePropertyName = sourcePropertyName,
                        CurrentValue = property.Value,
                        IsHeaderProperty = true
                    };
                }

                PropertiesTree.Items.Add(node);
            }

            var fileDataNode = new TreeViewItem
            {
                Header = "File Data",
                IsExpanded = true
            };

            var applicationNode = CreateApplicationNode();

            if (applicationNode != null)
                fileDataNode.Items.Add(applicationNode);

            fileDataNode.Items.Add(CreatePropertyNode(
                "Path",
                _currentFilePath ?? ""));

            fileDataNode.Items.Add(CreatePropertyNode(
                "Units",
                GetProjectUnits()));

            PropertiesTree.Items.Add(fileDataNode);
        }

        private void ShowEntityPropertiesGrouped(IfcRoot entity)
        {
            PropertiesTree.Items.Clear();

            Type? type = entity.GetType();

            while (type != null && type.Name.StartsWith("Ifc"))
            {
                var groupNode = new TreeViewItem
                {
                    Header = type.Name,
                    IsExpanded = true
                };

                var props = type.GetProperties()
                    .Where(p => p.DeclaringType == type)
                    .OrderBy(p => p.Name);

                foreach (var prop in props)
                {
                    if (!prop.CanRead)
                        continue;

                    try
                    {
                        var value = prop.GetValue(entity);

                        groupNode.Items.Add(CreatePropertyNode(
                            prop.Name,
                            FormatIfcValue(value)));
                    }
                    catch
                    {
                        // Некоторые служебные свойства xBIM могут не читаться напрямую.
                    }
                }

                if (groupNode.Items.Count > 0)
                    PropertiesTree.Items.Add(groupNode);

                type = type.BaseType;
            }

            AddPropertySetsToTree(entity);
            AddQuantitiesToTree(entity);
        }

        

        private void AddPropertySetsToTree(IfcRoot entity)
        {
            if (entity is not IfcObject ifcObject)
                return;

            foreach (var rel in ifcObject.IsDefinedBy)
            {
                if (rel is not IfcRelDefinesByProperties relProps)
                    continue;

                if (relProps.RelatingPropertyDefinition is not IfcPropertySet propertySet)
                    continue;

                var psetNode = new TreeViewItem
                {
                    Header = ToIfcText(propertySet.Name, "PropertySet"),
                    IsExpanded = true
                };

                psetNode.Tag = new PropertySetNode
                {
                    Relation = relProps,
                    PropertySet = propertySet,
                    OwnerEntity = ifcObject
                };

                foreach (var property in propertySet.HasProperties)
                {
                    if (property is IfcPropertySingleValue singleValue)
                    {
                        var propertyNode = CreatePropertyNode(
                             ToIfcText(singleValue.Name, ""),
                             singleValue.NominalValue?.ToString() ?? "");
                             propertyNode.Tag = new EditablePropertyNode
                        {
                            Name = ToIfcText(singleValue.Name, ""),
                            SourceObject = singleValue,
                            SourcePropertyName = "NominalValue"
                        };

                        psetNode.Items.Add(propertyNode);
                    }
                    else
                    {
                        psetNode.Items.Add(CreatePropertyNode(
                            ToIfcText(property.Name, ""),
                            property.ToString() ?? ""));
                    }
                }

                PropertiesTree.Items.Add(psetNode);
            }
        }

        private string ToIfcText(object? value, string fallback = "")
        {
            if (value == null)
                return fallback;

            var text = value.ToString();

            if (string.IsNullOrWhiteSpace(text))
                return fallback;

            return text;
        }
        private string FormatIfcValue(object? value)
        {
            if (value == null)
                return "";

            if (value is string text)
                return text;

            if (value is IEnumerable enumerable)
            {
                var items = new List<string>();

                foreach (var item in enumerable)
                {
                    if (item == null)
                        continue;

                    items.Add(item.ToString() ?? "");
                }

                return string.Join(", ", items);
            }

            return value.ToString() ?? "";
        }

        private void AddObjectProperties(List<IfcPropertyItem> list, string groupName, object obj)
        {
            foreach (var prop in obj.GetType().GetProperties())
            {
                if (!prop.CanRead)
                    continue;

                try
                {
                    var value = prop.GetValue(obj);

                    list.Add(new IfcPropertyItem
                    {
                        Name = $"{groupName}.{prop.Name}",
                        Value = FormatIfcValue(value)
                    });
                }
                catch
                {
                }
            }
        }

        private void AddPropertySets(List<IfcPropertyItem> properties, IfcRoot entity)
        {
            if (entity is not IfcObject ifcObject)
                return;

            foreach (var rel in ifcObject.IsDefinedBy)
            {
                var relProps = rel as IfcRelDefinesByProperties;

                if (relProps == null)
                    continue;

                var propertySet = relProps.RelatingPropertyDefinition as IfcPropertySet;

                if (propertySet == null)
                    continue;

                

                foreach (var property in propertySet.HasProperties)
                {
                    if (property is IfcPropertySingleValue singleValue)
                    {
                        properties.Add(new IfcPropertyItem
                        {
                            Name = $"{propertySet.Name}.{singleValue.Name}",
                            Value = singleValue.NominalValue?.ToString() ?? ""
                        });
                    }
                    else
                    {
                        properties.Add(new IfcPropertyItem
                        {
                            Name = $"{propertySet.Name}.{property.Name}",
                            Value = property.ToString()
                        });
                    }
                }
            }
        }

        private void AddQuantitiesToTree(IfcRoot entity)
        {
            if (entity is not IfcObject ifcObject)
                return;

            foreach (var rel in ifcObject.IsDefinedBy)
            {
                if (rel is not IfcRelDefinesByProperties relProps)
                    continue;

                if (relProps.RelatingPropertyDefinition is not IfcElementQuantity quantitySet)
                    continue;

                var quantityNode = new TreeViewItem
                {
                    Header = ToIfcText(quantitySet.Name, "ElementQuantity"),
                    IsExpanded = true
                };

                foreach (var quantity in quantitySet.Quantities)
                {
                    string value = "";

                    switch (quantity)
                    {
                        case IfcQuantityLength q:
                            value = q.LengthValue.ToString();
                            break;

                        case IfcQuantityArea q:
                            value = q.AreaValue.ToString();
                            break;

                        case IfcQuantityVolume q:
                            value = q.VolumeValue.ToString();
                            break;

                        case IfcQuantityCount q:
                            value = q.CountValue.ToString();
                            break;

                        case IfcQuantityWeight q:
                            value = q.WeightValue.ToString();
                            break;

                        case IfcQuantityTime q:
                            value = q.TimeValue.ToString();
                            break;

                        default:
                            value = quantity.ToString() ?? "";
                            break;
                    }

                    quantityNode.Items.Add(CreatePropertyNode(
                        ToIfcText(quantity.Name, ""),
                        value));
                }

                PropertiesTree.Items.Add(quantityNode);
            }
        }

        private void PropertiesTree_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_model == null)
                return;

            if (PropertiesTree.SelectedItem is not TreeViewItem item)
                return;

            if (item.Tag is not EditablePropertyNode editable)
                return;

            // 1. Редактирование File Header.
            // Сейчас разрешаем редактировать только FileName.OriginatingSystem.
            if (editable.IsHeaderProperty)
            {
                

                var window = new EditPropertyWindow(editable.Name, editable.CurrentValue)
                {
                    Owner = this
                };

                if (window.ShowDialog() != true)
                    return;

                if (window.NewValue == editable.CurrentValue)
                    return;

                _hasUnsavedChanges = true;
                UpdateWindowTitle();

                var prop = editable.SourceObject
                    .GetType()
                    .GetProperty(editable.SourcePropertyName);

                if (prop == null || !prop.CanWrite)
                {
                    MessageBox.Show("Это свойство File Header нельзя редактировать напрямую.");
                    return;
                }

                try
                {
                    using (var txn = _model.BeginTransaction("Edit header/application property"))
                    {
                        SetIfcPropertyValue(editable.SourceObject, prop, window.NewValue);

                        if (editable.SourceObject is Xbim.Ifc2x3.UtilityResource.IfcApplication)
                            SyncOriginatingSystemFromApplication();

                        txn.Commit();
                    }

                    editable.CurrentValue = window.NewValue;
                    _hasUnsavedChanges = true;

                    ShowFileHeaderProperties();

                    
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        "Ошибка изменения File Header:\n" +
                        ex.Message + "\n\n" +
                        "Inner:\n" +
                        ex.InnerException?.Message);
                }

                return;
            }

            // 2. Редактирование обычного IfcPropertySingleValue из PropertySet.
            if (editable.SourceObject is not IfcPropertySingleValue singleValue)
                return;

            string currentValue = singleValue.NominalValue?.ToString() ?? "";

            var editWindow = new EditPropertyWindow(editable.Name, currentValue)
            {
                Owner = this
            };

            if (editWindow.ShowDialog() != true)
                return;

            string newValue = editWindow.NewValue;

            if (newValue == currentValue)
                return;

            _hasUnsavedChanges = true;
            UpdateWindowTitle();

            try
            {
                using (var txn = _model.BeginTransaction("Edit property value"))
                {
                    singleValue.NominalValue = new IfcText(newValue);
                    txn.Commit();
                }

                _hasUnsavedChanges = true;

                item.Header = CreatePropertyHeader(editable.Name, newValue);

                
            }
            catch (Exception ex)
            {
                MessageBox.Show("Ошибка изменения свойства:\n" + ex.Message);
            }
        }
        private Grid CreatePropertyHeader(string name, string value)
        {
            var panel = new Grid();

            panel.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(250)
            });

            panel.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = GridLength.Auto
            });

            var nameBlock = new TextBlock
            {
                Text = name,
                FontWeight = FontWeights.Normal
            };

            Grid.SetColumn(nameBlock, 0);

            var valueBlock = new TextBlock
            {
                Text = value,
                Margin = new Thickness(10, 0, 0, 0)
            };

            Grid.SetColumn(valueBlock, 1);

            panel.Children.Add(nameBlock);
            panel.Children.Add(valueBlock);

            return panel;
        }

        private bool TryGetHeaderEditTarget(string fullName, out object sourceObject, out string propertyName)
        {
            sourceObject = null!;
            propertyName = "";

            if (_model == null)
                return false;

            if (fullName.StartsWith("FileName."))
            {
                sourceObject = _model.Header.FileName;
                propertyName = fullName.Replace("FileName.", "");
                return true;
            }

            if (fullName.StartsWith("FileDescription."))
            {
                sourceObject = _model.Header.FileDescription;
                propertyName = fullName.Replace("FileDescription.", "");
                return true;
            }

            if (fullName.StartsWith("FileSchema."))
            {
                sourceObject = _model.Header.FileSchema;
                propertyName = fullName.Replace("FileSchema.", "");
                return true;
            }

            return false;
        }
        private TreeViewItem CreateEntityNode(IfcRoot entity)
        {
            string name = entity.Name?.ToString() ?? "";

            if (string.IsNullOrWhiteSpace(name))
                name = "(без имени)";

            return new TreeViewItem
            {
                Header = $"{entity.GetType().Name}: {name}  #{entity.EntityLabel}",
                Tag = entity
            };
        }

        private void SaveAsIfc_Click(object sender, RoutedEventArgs e)
        {
            if (_model == null)
            {
                MessageBox.Show("Сначала откройте IFC-файл.");
                return;
            }

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "IFC files (*.ifc)|*.ifc|All files (*.*)|*.*",
                DefaultExt = ".ifc",
                FileName = "edited.ifc"
            };

            if (dialog.ShowDialog() != true)
                return;

            try
            {
                _model.SaveAs(dialog.FileName);

                _currentFilePath = dialog.FileName;
                _hasUnsavedChanges = false;

                MessageBox.Show("IFC-файл сохранен как новая копия.");
                _hasUnsavedChanges = false;
                UpdateWindowTitle();

            }
            catch (Exception ex)
            {
                MessageBox.Show("Ошибка сохранения IFC-файла:\n" + ex.Message);
            }
        }


        private string GetApplicationName()
        {
            if (_model == null)
                return "";

            return _model.Header.FileName.OriginatingSystem ?? "";

        }

        private string GetProjectUnits()
        {
            if (_model == null)
                return "";

            var project = _model.Instances
                .OfType<IfcProject>()
                .FirstOrDefault();

            if (project?.UnitsInContext == null)
                return "";

            return project.UnitsInContext.ToString() ?? "";
        }

        private void UpdateIfcApplicationName(string newValue)
        {
            if (_model == null)
                return;

            var applications = _model.Instances
                .OfType<Xbim.Ifc2x3.UtilityResource.IfcApplication>()
                .ToList();

            using (var txn = _model.BeginTransaction("Update IfcApplication"))
            {
                foreach (var app in applications)
                {
                    app.ApplicationFullName = newValue;
                }

                txn.Commit();
            }
        }

        private TreeViewItem? CreateApplicationNode()
        {
            if (_model == null)
                return null;

            var app = _model.Instances
                .OfType<Xbim.Ifc2x3.UtilityResource.IfcApplication>()
                .FirstOrDefault();

            if (app == null)
                return null;

            var appNode = new TreeViewItem
            {
                Header = "Application",
                IsExpanded = true
            };

            AddEditableApplicationProperty(appNode, app, "ApplicationFullName", app.ApplicationFullName);
            AddEditableApplicationProperty(appNode, app, "Version", app.Version);
            AddEditableApplicationProperty(appNode, app, "ApplicationIdentifier", app.ApplicationIdentifier);

            return appNode;
        }

        private void AddEditableApplicationProperty(TreeViewItem parentNode, Xbim.Ifc2x3.UtilityResource.IfcApplication app, string propertyName, string value)
        {
            var node = CreatePropertyNode(propertyName, value);

            node.Tag = new EditablePropertyNode
            {
                Name = propertyName,
                SourceObject = app,
                SourcePropertyName = propertyName,
                CurrentValue = value,
                IsHeaderProperty = true
            };

            parentNode.Items.Add(node);
        }

        private void SetIfcPropertyValue(object sourceObject, System.Reflection.PropertyInfo prop, string newValue)
        {
            if (prop.PropertyType == typeof(string))
            {
                prop.SetValue(sourceObject, newValue);
                return;
            }

            if (prop.PropertyType == typeof(IfcLabel))
            {
                prop.SetValue(sourceObject, new IfcLabel(newValue));
                return;
            }

            if (prop.PropertyType == typeof(IfcIdentifier))
            {
                prop.SetValue(sourceObject, new IfcIdentifier(newValue));
                return;
            }

            var currentObject = prop.GetValue(sourceObject);

            if (currentObject is System.Collections.IList list)
            {
                list.Clear();
                list.Add(newValue);
                return;
            }

            throw new InvalidOperationException(
                $"Редактирование свойства типа {prop.PropertyType.Name} пока не поддерживается.");
        }
        private void SyncOriginatingSystemFromApplication()
        {
            if (_model == null)
                return;

            var app = _model.Instances
                .OfType<Xbim.Ifc2x3.UtilityResource.IfcApplication>()
                .FirstOrDefault();

            if (app == null)
                return;

            var appName = app.ApplicationFullName.ToString();
            var version = app.Version.ToString();

            var result = string.IsNullOrWhiteSpace(version)
                ? appName
                : $"{appName} Build {version}";

            _model.Header.FileName.OriginatingSystem = result;
        }

        private void UpdateWindowTitle()
        {
            Title = _hasUnsavedChanges
                ? "IFC Property Editor *"
                : "IFC Property Editor";
        }

        private void PropertiesTree_PreviewMouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var item = FindParentTreeViewItem(e.OriginalSource as DependencyObject);

            if (item == null)
                return;

            item.IsSelected = true;

            if (item.Tag is not PropertySetNode psetNode)
                return;

            var menu = new ContextMenu();

            var deleteItem = new MenuItem
            {
                Header = "Удалить PropertySet"
            };

            deleteItem.Click += (s, args) =>
            {
                DeletePropertySet(psetNode);
            };

            menu.Items.Add(deleteItem);
            item.ContextMenu = menu;
        }

        private TreeViewItem? FindParentTreeViewItem(DependencyObject? source)
        {
            while (source != null)
            {
                if (source is TreeViewItem item)
                    return item;

                source = System.Windows.Media.VisualTreeHelper.GetParent(source);
            }

            return null;
        }

        private void DeletePropertySet(PropertySetNode node)
        {
            if (_model == null)
                return;

            var result = MessageBox.Show(
                $"Удалить PropertySet \"{ToIfcText(node.PropertySet.Name, "PropertySet")}\" у выбранного элемента?",
                "Удаление PropertySet",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
                return;

            try
            {
                using (var txn = _model.BeginTransaction("Delete PropertySet relation"))
                {
                    node.Relation.RelatedObjects.Remove(node.OwnerEntity);
                    txn.Commit();
                }

                _hasUnsavedChanges = true;
                UpdateWindowTitle();

                if (EntitiesTree.SelectedItem is TreeViewItem selectedEntityNode &&
                    selectedEntityNode.Tag is IfcRoot entity)
                {
                    ShowEntityPropertiesGrouped(entity);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Ошибка удаления PropertySet:\n" + ex.Message);
            }
        }
    }

}