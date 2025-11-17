using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using System;
using System.Reflection;
using JetBrains.Annotations;
using UnityEngine.UIElements;
using UnityEditor.Experimental.GraphView;
using UnityEditor.ShortcutManagement;
/*
Author : Cyanilux (https://twitter.com/Cyanilux)
Github Repo : https://github.com/Cyanilux/ShaderGraphVariables

Extra Features :
	- Colour of groups can be changed
		- Right-click group the group name and select Edit Group Colour
		- A colour picker should open, allowing you to choose a colour (including alpha)
		- Behind the scenes this creates a hidden Color node in the group. It can only be moved with
			the group so try to move the group as a whole rather than nodes inside it.
	- 'Port Swap' Hotkey
		- "S" key will swap the first two ports (usually A and B) on all currently selected nodes
		- Enabled for nodes : Add, Subtract, Multiply, Divide, Maximum, Minimum, Lerp, Inverse Lerp, Step and Smoothstep
		- Note : It needs at least one connection to swap. If both ports are empty, it won't swap the values as it doesn't
			update properly and I couldn't find a way to force-update it.
	- 'Add Node' Hotkeys (Default : Alpha Number keys, 1 to 0)
		- Bind 10 hotkeys for quickly adding specific nodes to the graph (at mouse position)
		- Defaults are :
			- 1 : Add
			- 2 : Subtract
			- 3 : Multiply
			- 4 : Lerp
			- 5 : Split
			- 6 : One Minus
			- 7 : Negate
			- 8 : Absolute
			- 9 : Step
			- 0 : Smoothstep
		- To change nodes : Tools → SGVariablesExtraFeatures → Rebind Node Bindings
	- To edit keybindings : Edit → Shortcuts (search for SGVariables)
		- Note, try to avoid conflicts with SG's hotkeys (mainly A, F and O) as those can't be rebound
		- https://www.cyanilux.com/tutorials/intro-to-shader-graph/#shortcuts

*/
using static SGV.SGVariableManger;

namespace SGV
{
	[PublicAPI]
	public class ExtraFeatures
	{
		#region Extra Features (Swap Command)

		[MenuItem("Tools/Shader Graph Variables/Settings/Nodes/Swap Ports On Selected Nodes _s")]
		private static void SwapPortsCommand()
		{
			if (!m_sgHasFocus || m_graphView == null) return;
			if (m_debugMessages) Debug.Log("Swap Ports");

			var selected = m_graphView.selection;
			foreach (var s in selected)
			{
				if (s is Node)
				{
					var node = (Node)s;
					var materialNode = NodeToSGMaterialNode(node);
					var type = materialNode.GetType().ToString();
					type = type[(type.LastIndexOf('.') + 1)..];

					if (type == "AddNode" || type == "SubtractNode" ||
					    type == "MultiplyNode" || type == "DivideNode" ||
					    type == "MaximumNode" || type == "MinimumNode" ||
					    type == "LerpNode" || type == "InverseLerpNode" ||
					    type == "StepNode" || type == "SmoothstepNode")
					{
						var inputPorts = GetInputPorts(node);
						var a = inputPorts.AtIndex(0);
						var b = inputPorts.AtIndex(1);

						// Swap connections
						var connectedA = GetConnectedPort(a);
						var connectedB = GetConnectedPort(b);
						DisconnectAllInputs(node);
						if (connectedA != null) Connect(connectedA, b);
						if (connectedB != null) Connect(connectedB, a);

						// Swap values (the values used if no port is connected)
						if (connectedA != null || connectedB != null)
						{
							// Node doesn't update properly unless at least one connection swaps
							var aSlot = GetMaterialSlot(a);
							var bSlot = GetMaterialSlot(b);

							var valueProperty = aSlot?.GetType().GetProperty("value");

							var valueA = valueProperty?.GetValue(aSlot);
							var valueB = valueProperty?.GetValue(bSlot);
							valueProperty?.SetValue(aSlot, valueB);
							valueProperty?.SetValue(bSlot, valueA);
						}
					}
				}
			}
		}

		#endregion

		#region Extra Features (Add Node Commands)

		private class AddNodeType
		{
			public readonly Type Type;
			public readonly string SubgraphGUID; // guid if type is UnityEditor.ShaderGraph.SubGraphNode

			public AddNodeType(Type type, string subgraphGUID)
			{
				Type = type;
				SubgraphGUID = subgraphGUID;
			}
		}

		private static AddNodeType[] addNodeTypes = new AddNodeType[12];

		private static readonly string[] addNodeDefaults = new[]
		{
			"Get",
			"Set",
			"Add",
			"Subtract",
			"Multiply",
			"Lerp",
			"Split",
			"OneMinus",
			"Negate",
			"Absolute",
		};

		#region Extra Features (Rebind)

		private class RebindWindow : EditorWindow
		{
			private const string c_shortcutID = "Main Menu/Tools/Shader Graph Variables/Nodes/Add Node ";
			private string[] m_shortcuts;
			private string[] m_values;

			//private IShortcutManager shortcutManager;

			private void OnEnable()
			{
				var shortcutManager = ShortcutManager.instance;
				m_shortcuts = new string[addNodeDefaults.Length];
				m_values = new string[addNodeDefaults.Length];
				for (var i = 0; i < addNodeDefaults.Length; i++)
				{
					var shortcut = shortcutManager.GetShortcutBinding(c_shortcutID + (i + 1));
					m_shortcuts[i] = shortcut.ToString();

					var s = EditorPrefs.GetString("SGVariables_Node" + (i + 1));
					if (string.IsNullOrWhiteSpace(s))
					{
						s = addNodeDefaults[i];
					}

					m_values[i] = s;
				}
			}

			private void OnGUI()
			{
				EditorGUILayout.LabelField("Add Node Bindings", EditorStyles.boldLabel);
				EditorGUILayout.LabelField("Note : You can also change the hotkeys in Edit -> Shortcuts, search for SGVariables", EditorStyles.wordWrappedLabel);
				EditorGUILayout.LabelField("The values here should match the class used by a node. Should be the same as the node name, without spaces. "
				                         + "Won't support pipeline-specific nodes, only searches the UnityEngine.ShaderGraph namespace.", EditorStyles.wordWrappedLabel);
				EditorGUILayout.LabelField("Also supports 'Set' and 'Get', for other SubGraphs use \"SubGraph(<guid>)\", "
				                         + "where guid is the string located at the top of the .meta file associated with the SubGraph.", EditorStyles.wordWrappedLabel);
				for (var i = 0; i < addNodeDefaults.Length; i++)
				{
					EditorGUI.BeginChangeCheck();
					var s = EditorGUILayout.TextField("Node Binding " + (i + 1) + " (" + m_shortcuts[i] + ")", m_values[i]);
					if (EditorGUI.EndChangeCheck())
					{
						EditorPrefs.SetString("SGVariables_Node" + (i + 1), System.Text.RegularExpressions.Regex.Replace(s, @"\s+", ""));
						m_values[i] = s;
						ClearTypes();
					}
				}

				EditorGUILayout.LabelField("To reset to defaults, leave fields blank", EditorStyles.wordWrappedLabel);
			}

			private void ClearTypes()
			{
				addNodeTypes = new AddNodeType[addNodeDefaults.Length];
			}
		}

		[MenuItem("Tools/Shader Graph Variables/Settings/Modify Shortcut Nodes", false, 0)]
		private static void RebindNodes()
		{
			var window = (RebindWindow)EditorWindow.GetWindow(typeof(RebindWindow));
			window.Show();
		}

		#endregion

		[MenuItem("Tools/Shader Graph Variables/Settings/Nodes/Add Node 1 _1", priority = 1)]
		private static void AddNodeCommand1()
		{
			AddNodeCommand(1);
		}

		[MenuItem("Tools/Shader Graph Variables/Settings/Nodes/Add Node 2 _2", priority = 2)]
		private static void AddNodeCommand2()
		{
			AddNodeCommand(2);
		}

		[MenuItem("Tools/Shader Graph Variables/Settings/Nodes/Add Node 3 _3", priority = 3)]
		private static void AddNodeCommand3()
		{
			AddNodeCommand(3);
		}

		[MenuItem("Tools/Shader Graph Variables/Settings/Nodes/Add Node 4 _4", priority = 4)]
		private static void AddNodeCommand4()
		{
			AddNodeCommand(4);
		}

		[MenuItem("Tools/Shader Graph Variables/Settings/Nodes/Add Node 5 _5", priority = 5)]
		private static void AddNodeCommand5()
		{
			AddNodeCommand(5);
		}

		[MenuItem("Tools/Shader Graph Variables/Settings/Nodes/Add Node 6 _6", priority = 6)]
		private static void AddNodeCommand6()
		{
			AddNodeCommand(6);
		}

		[MenuItem("Tools/Shader Graph Variables/Settings/Nodes/Add Node 7 _7", priority = 7)]
		private static void AddNodeCommand7()
		{
			AddNodeCommand(7);
		}

		[MenuItem("Tools/Shader Graph Variables/Settings/Nodes/Add Node 8 _8", priority = 8)]
		private static void AddNodeCommand8()
		{
			AddNodeCommand(8);
		}

		[MenuItem("Tools/Shader Graph Variables/Settings/Nodes/Add Node 9 _9", priority = 9)]
		private static void AddNodeCommand9()
		{
			AddNodeCommand(9);
		}

		[MenuItem("Tools/Shader Graph Variables/Settings/Nodes/Add Node 10 _0", priority = 10)]
		private static void AddNodeCommand10()
		{
			AddNodeCommand(10);
		}
		

		private static void AddNodeCommand(int i)
		{
			if (!m_sgHasFocus || m_graphView == null) return;
			var type = addNodeTypes[i - 1];
			if (type == null)
			{
				var node = EditorPrefs.GetString("SGVariables_Node" + i);
				if (string.IsNullOrWhiteSpace(node))
				{
					// Use default
					node = addNodeDefaults[i - 1];
				}

				string subgraphGUID = null;
				if (node.Contains("SubGraph"))
				{
					var start = node.IndexOf('(');
					var end = node.LastIndexOf(')');
					var length = end - start - 1;
					subgraphGUID = node.Substring(start + 1, length);
					node = "SubGraph";
				}
				else if (node == "Set")
				{
					subgraphGUID = "d455b29bada2b284ca73133c44fbc1ce";
					node = "SubGraph";
				}
				else if (node == "Get")
				{
					subgraphGUID = "5951f0cfb2fb4134ea014f63adeff8d9";
					node = "SubGraph";
				}

				var typeString = "UnityEditor.ShaderGraph." + node + "Node";
				type = new AddNodeType(sgAssembly.GetType(typeString), subgraphGUID);
				addNodeTypes[i - 1] = type;
				if (type == null)
				{
					Debug.LogWarning("Type " + typeString + " does not exist");
				}
			}

			var mousePos = Event.current.mousePosition;
			mousePos.y -= 35;
			var matrix = m_graphView.viewTransform.matrix.inverse;
			var r = new Rect
			{
				position = matrix.MultiplyPoint(mousePos)
			};
			AddNode(type, r);
		}

		private static PropertyInfo drawStateProperty;
		private static PropertyInfo positionProperty;
		private static PropertyInfo expandedProperty;
		private static PropertyInfo subGraphAssetProperty;

		private static MethodInfo addNodeMethod;

		private static void AddNode(AddNodeType addNodeType, Rect position)
		{
			if (addNodeType == null) return;
			var type = addNodeType.Type;
			if (type == null) return;
			var nodeToAdd = Activator.CreateInstance(type);
			if (nodeToAdd == null)
			{
				Debug.LogWarning("Could not create node of type " + type);
			}

			// https://github.com/Unity-Technologies/Graphics/blob/master/com.unity.shadergraph/Editor/Data/Nodes/AbstractMaterialNode.cs

			if (drawStateProperty == null)
			{
				drawStateProperty = abstractMaterialNodeType.GetProperty("drawState", bindingFlags); // Type : DrawState
			}

			var drawState = drawStateProperty?.GetValue(nodeToAdd);
			if (positionProperty == null)
			{
				positionProperty = drawState?.GetType().GetProperty("position", bindingFlags);
			}

			if (expandedProperty == null)
			{
				expandedProperty = drawState?.GetType().GetProperty("expanded", bindingFlags);
			}

			positionProperty?.SetValue(drawState, position);
			drawStateProperty?.SetValue(nodeToAdd, drawState);

			// Handle SubGraph GUID
			var subgraphGUID = addNodeType.SubgraphGUID;
			if (subgraphGUID != null)
			{
				if (subGraphAssetProperty == null)
				{
					subGraphAssetProperty = type.GetProperty("asset");
				}

				var asset = GetSubGraphAsset(subgraphGUID);
				if (asset != null)
				{
					subGraphAssetProperty?.SetValue(nodeToAdd, asset);
				}
			}

			// GraphData.AddNode(abstractMaterialNode)
			if (addNodeMethod == null)
			{
				addNodeMethod = graphDataType.GetMethod("AddNode", bindingFlags);
			}

			addNodeMethod.Invoke(graphData, new[] { nodeToAdd });

			if (m_debugMessages) Debug.Log("Added Node of Type " + type.ToString());
		}

		// Support SubGraphs
		private static Type subGraphAssetType;

		public static object GetSubGraphAsset(string guidString)
		{
			if (subGraphAssetType == null)
			{
				subGraphAssetType = sgAssembly.GetType("UnityEditor.ShaderGraph.SubGraphAsset");
			}

			return AssetDatabase.LoadAssetAtPath(AssetDatabase.GUIDToAssetPath(guidString), subGraphAssetType);
		}

		#endregion

		#region Extra Features (Group Colours)

		private static string GetSerializedValue(Node node)
		{
			var materialNode = NodeToSGMaterialNode(node);
			if (materialNode != null)
			{
				var synonyms = GetSerializedValues(materialNode);
				if (synonyms != null && synonyms.Length > 0)
				{
					return synonyms[0];
				}
			}

			return "";
		}

		private static void SetSerializedValue(Node node, string key)
		{
			var materialNode = NodeToSGMaterialNode(node);
			SetSerializedValues(materialNode, new[] { key });
		}

		internal static void UpdateExtraFeatures()
		{
			if (!m_sgHasFocus) return;
			//if (loadVariables) { // (first time load, but we kinda need to constantly check as groups could be copied)
			// Load Group Colours
			m_graphView.nodes.ForEach(node =>
			{
				if (node.title.Equals("Color") && node.visible)
				{
					if (GetSerializedValue(node) == "GroupColor")
					{
						var scope = node.GetContainingScope();
						if (scope == null)
						{
							// Node is not in group, the group may have been deleted.
							// GraphData.RemoveNode(abstractMaterialNode);
							RemoveNode(node);
							return;
						}

						node.visible = false;
						node.style.maxWidth = 100;
						SetGroupColor(scope as Group, node, GetGroupNodeColor(node));
					}
				}
			});
			//}

			// As we right-click group, add the manipulator
			var selected = m_graphView.selection;
			var groupSelected = false;
			foreach (var s in selected)
			{
				if (s is Group)
				{
					var group = (Group)s;
					groupSelected = true;

					// Set selection colour (if overriden)
					if (group.style.borderTopColor != StyleKeyword.Null)
					{
						var selectedColor = new Color(0.266f, 0.75f, 1, 1);
						group.style.borderTopColor = selectedColor;
						group.style.borderBottomColor = selectedColor;
						group.style.borderLeftColor = selectedColor;
						group.style.borderRightColor = selectedColor;
					}

					if (editingGroup == s) break;

					if (editingGroup != null)
					{
						// (if switched to a different group)
						if (editingGroup.style.borderTopColor != StyleKeyword.Null)
						{
							var colorNode = GetGroupColorNode(GetGroupGuid(editingGroup));
							if (colorNode != null)
							{
								var groupColor = GetGroupNodeColor(colorNode);
								editingGroup.style.borderTopColor = groupColor;
								editingGroup.style.borderBottomColor = groupColor;
								editingGroup.style.borderLeftColor = groupColor;
								editingGroup.style.borderRightColor = groupColor;
							}
						}

						editingGroup.RemoveManipulator(manipulator);
					}

					group.AddManipulator(manipulator);
					editingGroup = group;
					break;
				}
			}

			if (!groupSelected && editingGroup != null)
			{
				// (if group was unselected)
				if (editingGroup.style.borderTopColor != StyleKeyword.Null)
				{
					var colorNode = GetGroupColorNode(GetGroupGuid(editingGroup));
					if (colorNode != null)
					{
						var groupColor = GetGroupNodeColor(colorNode);
						editingGroup.style.borderTopColor = groupColor;
						editingGroup.style.borderBottomColor = groupColor;
						editingGroup.style.borderLeftColor = groupColor;
						editingGroup.style.borderRightColor = groupColor;
					}
				}

				editingGroup = null;
			}

			// User clicked context menu, show Color Picker
			if (editingGuid != null)
			{
				var colorNode = GetGroupColorNode(editingGuid);
				editingGroupColorNode = colorNode;
				if (colorNode != null)
				{
					if (colorNode.visible)
					{
						colorNode.visible = false;
						colorNode.style.maxWidth = 100;
					}

					ShowColorPicker(GroupColourChanged, GetGroupNodeColor(editingGroupColorNode), true, false);

					editingGuid = null;
				}
			}
			else
			{
				editingGroupColorNode = null;
			}
		}

		private static Type colorPickerType;

		private static void GetColorPickerType()
		{
			// pretty hacky
			var assembly = Assembly.Load(new AssemblyName("UnityEditor"));
			colorPickerType = assembly.GetType("UnityEditor.ColorPicker");
		}

		private static MethodInfo showColorPicker;

		public static void ShowColorPicker(Action<Color> action, Color initialColor, bool showAlpha, bool hdr)
		{
			// Couldn't figure out a way to show the colour picker from UIElements, so... reflection!
			// Show(Action<Color> colorChangedCallback, Color col, bool showAlpha = true, bool hdr = false)

			if (colorPickerType == null) GetColorPickerType();
			if (showColorPicker == null)
			{
				showColorPicker = colorPickerType?.GetMethod("Show",
				                                             new[] { typeof(Action<Color>), typeof(Color), typeof(bool), typeof(bool) }
				);
			}

			showColorPicker?.Invoke(null, new object[] { action, initialColor, showAlpha, hdr });
		}

		private static void BuildContextualMenu(ContextualMenuPopulateEvent evt)
		{
			evt.menu.AppendSeparator();
			evt.menu.AppendAction("Edit Group Color", OnMenuAction, DropdownMenuAction.AlwaysEnabled);
		}

		private static void OnMenuAction(DropdownMenuAction action)
		{
			if (editingGroup == null) return;
			//Debug.Log("OnMenuAction " + editingGroup.title);

			var guid = GetGroupGuid(editingGroup);
			if (guid == null) return;

			var colorNode = GetGroupColorNode(guid);
			if (colorNode == null)
			{
				// We store the group colour in a hidden colour node
				CreateGroupColorNode(editingGroup);

				// We need to wait until this node is actually created,
				// so we'll store the guid in editingGuid and handle the rest
				// in UpdateExtraFeatures()
			}

			editingGuid = guid;
		}

		private static ContextualMenuManipulator manipulator = new(BuildContextualMenu);

		private static Group editingGroup;
		private static string editingGuid;
		private static Node editingGroupColorNode;

		private static PropertyInfo groupGuidField;
		private static PropertyInfo colorProperty;
		private static PropertyInfo groupProperty;
		private static PropertyInfo previewExpandedProperty;

		private static FieldInfo colorField;

		private static void CreateGroupColorNode(Group group)
		{
			var groupData = group.userData;
			if (groupData == null) return;

			var nodeToAdd = Activator.CreateInstance(colorNodeType); // Type : ColorNode

			// https://github.com/Unity-Technologies/Graphics/blob/master/com.unity.shadergraph/Editor/Data/Nodes/AbstractMaterialNode.cs

			if (synonymsField == null) synonymsField = abstractMaterialNodeType.GetField("synonyms");
			synonymsField.SetValue(nodeToAdd, new[] { "GroupColor" });

			if (colorProperty == null) colorProperty = colorNodeType.GetProperty("color"); // SG Color struct, not UnityEngine.Color
			var colorStruct = colorProperty?.GetValue(nodeToAdd);
			if (colorField == null)
			{
				colorField = colorStruct?.GetType().GetField("color");
			}

			colorField?.SetValue(colorStruct, new Color(0.1f, 0.1f, 0.1f, 0.1f));
			colorProperty?.SetValue(nodeToAdd, colorStruct);

			if (groupProperty == null)
				groupProperty = abstractMaterialNodeType.GetProperty("group", bindingFlags); // Type : GroupData
			if (drawStateProperty == null)
				drawStateProperty = abstractMaterialNodeType.GetProperty("drawState", bindingFlags); // Type : DrawState
			if (previewExpandedProperty == null)
				previewExpandedProperty = abstractMaterialNodeType.GetProperty("previewExpanded", bindingFlags); // Type : Bool

			previewExpandedProperty?.SetValue(nodeToAdd, false);

			var drawState = drawStateProperty.GetValue(nodeToAdd);
			if (positionProperty == null)
				positionProperty = drawState.GetType().GetProperty("position", bindingFlags);
			if (expandedProperty == null)
				expandedProperty = drawState.GetType().GetProperty("expanded", bindingFlags);

			var r = group.GetPosition();
			r.x += 25;
			r.y += 60;
			positionProperty.SetValue(drawState, r);
			expandedProperty.SetValue(drawState, false);
			drawStateProperty.SetValue(nodeToAdd, drawState);
			groupProperty.SetValue(nodeToAdd, groupData);

			// GraphData.AddNode(abstractMaterialNode)
			if (addNodeMethod == null)
			{
				addNodeMethod = graphDataType.GetMethod("AddNode", bindingFlags);
			}

			addNodeMethod.Invoke(graphData, new[] { nodeToAdd });
		}

		private static string GetGroupGuid(Scope scope)
		{
			var groupData = scope.userData;
			if (groupData == null) return null;
			if (groupGuidField == null) groupGuidField = groupData.GetType().GetProperty("objectId", bindingFlags);
			var groupGuid = (string)groupGuidField?.GetValue(groupData);
			//Debug.Log("GroupGuid : " + groupGuid);
			return groupGuid;
		}

		private static Node GetGroupColorNode(string guid)
		{
			var nodes = m_graphView.nodes.ToList();
			foreach (var node in nodes)
			{
				if (node.title.Equals("Color"))
				{
					var scope = node.GetContainingScope();
					if (scope == null) continue; // Node is not in group
					if (GetGroupGuid(scope) == guid && GetSerializedValue(node) == "GroupColor")
					{
						//Debug.Log("Found node in group~");
						return node;
					}
				}
			}

			return null;
		}

		private static Color GetGroupNodeColor(Node node)
		{
			var materialNode = NodeToSGMaterialNode(node);
			if (colorProperty == null) colorProperty = colorNodeType.GetProperty("color"); // SG Color struct, not UnityEngine.Color
			var colorStruct = colorProperty.GetValue(materialNode);
			if (colorField == null) colorField = colorStruct.GetType().GetField("color");
			return (Color)colorField.GetValue(colorStruct);
		}

		private static void SetGroupColor(Group group, Node groupNode, Color color)
		{
			group.style.backgroundColor = color;

			// Change border too
			group.style.borderTopColor = color;
			group.style.borderBottomColor = color;
			group.style.borderLeftColor = color;
			group.style.borderRightColor = color;

			var materialNode = NodeToSGMaterialNode(groupNode);
			if (colorProperty == null) colorProperty = colorNodeType.GetProperty("color"); // SG Color struct, not UnityEngine.Color
			var colorStruct = colorProperty.GetValue(materialNode);
			if (colorField == null) colorField = colorStruct.GetType().GetField("color");
			colorField.SetValue(colorStruct, color);
			colorProperty.SetValue(materialNode, colorStruct);

			var label = group.Query<UnityEngine.UIElements.Label>().First();
			if (color.grayscale > 0.5 && color.a > 0.4)
			{
				label.style.color = Color.black;
			}
			else
			{
				label.style.color = Color.white;
			}
		}

		private static void GroupColourChanged(Color color)
		{
			SetGroupColor(editingGroup, editingGroupColorNode, color);
		}

		#endregion
	}
}