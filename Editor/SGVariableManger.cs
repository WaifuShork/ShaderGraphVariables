using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using JetBrains.Annotations;
using UnityEngine.UIElements;
using UnityEditor.Experimental.GraphView;
using UnityEditor.Rendering;

/*
Author : Cyanilux (https://twitter.com/Cyanilux)
Github Repo : https://github.com/Cyanilux/ShaderGraphVariables

Main Feature :
	- Adds 'Set' and 'Get' nodes to Shader Graph, allowing you to link sections of a graph without connection wires.
		- These nodes (technically subgraphs) include a TextField where you can enter a variable name (not case sensitive).
		- They automatically link up with invisible connections/wires/edges.
		- These variables are local to the graph - they won't be shared between other graphs or subgraphs.
	- Supports Float, Vector2, Vector3 and Vector4 types.
	- The variable names are serialized using the node's "synonyms" field, which is unused by the graph (only used for nodes in the Add Node menu).
	  If the tool is removed the graph should still load correctly. However it does use a few SubGraphs and if they don't exist you'll need to
	  remove those nodes, reinstall the tool, or at least include the SubGraphs from the tool in your Assets.

Extra Features : (see ExtraFeatures.cs for more info)
	- Group Colors (Right-click group name)
	- 'Port Swap' Hotkey (Default : S)
	- 'Add Node' Hotkeys (Default : Alpha Number keys, 1 to 0)
		- To change nodes : Tools → SGVariablesExtraFeatures → Rebind Node Bindings

	- To edit keybindings : Edit → Shortcuts (search for SGVariables)
		- Note, try to avoid conflicts with SG's hotkeys (mainly A, F and O) as those can't be rebound
		- https://www.cyanilux.com/tutorials/intro-to-shader-graph/#shortcuts

Setup:
	- Install via Package Manager → Add package via git URL : https://github.com/Cyanilux/ShaderGraphVariables.git
	- Alternatively, download and put the folder in your Assets

Usage :
	1) Add Node → 'Set'
		- The node has a text field in the place of it's output port where you can type a variable name.
		- Attach a Float/Vector to the input port.
	2) Add Node → Get Variable
		- Again, it has a text field but this time for the input port. Type the same variable name
		- Variable names aren't case sensitive. "Example" would stil link to "EXAMPLE" or "ExAmPlE" etc.
		- When the variable name matches, the in-line input port value (e.g. (0,0,0,0)) should disappear and the preview will change.
		- A connection/edge may blink temporarily, but then is hidden to keep the graph clean.
		- You can now use the output of that node as you would with anything else.

Known Issues :
	- If a 'Get Variable' node is connected to the vertex stage and then a name is entered, it can cause shader errors
		if fragment-only nodes are used by the variable (e.g. cannot map expression to vs_5_0 instruction set)
	- If a DynamicVector/DynamicValue output port (most math nodes) changes type (because of it's inputs), it wont update type of Get/'Set' nodes connected previously.
	- Got an issue, check : https://github.com/Cyanilux/ShaderGraphVariables/issues, if it's not there, add it!
*/

namespace SGV
{
	[InitializeOnLoad]
	public class SGVariableManger
	{
		// Debug ----------------------------------------------
		internal static readonly bool m_debugMessages = false;

		private static readonly bool m_disableTool = false;
		private static readonly bool m_disableVariableNodes = false;
		private static readonly bool m_disableExtraFeatures = false;
		private static readonly bool m_debugPutTextFieldAboveNode = false;
		private static readonly bool m_debugDontHideEdges = false;

		//	----------------------------------------------------

		// Sorry if the code is badly organised~

		// Sources
		// https://github.com/Unity-Technologies/Graphics/tree/master/com.unity.shadergraph/Editor
		// https://github.com/Unity-Technologies/UnityCsReference/tree/master/Modules/GraphViewEditor

		private static float m_initTime;
		private static bool m_isEnabled;
		//private static bool revalidateGraph = false;

		static SGVariableManger()
		{
			if (m_disableTool)
			{
				return;
			}
			
			Start();
		}

		private static void Start()
		{
			if (m_isEnabled)
			{
				return;
			}

			m_initTime = Time.realtimeSinceStartup;
			EditorApplication.update += CheckForGraphs;
			Undo.undoRedoPerformed += OnUndoRedo;
			m_isEnabled = true;
		}

		public static void Stop()
		{
			EditorApplication.update -= CheckForGraphs;
			Undo.undoRedoPerformed -= OnUndoRedo;
			m_isEnabled = false;
		}

		internal static EditorWindow m_sgWindow;
		private static EditorWindow m_prev;
		internal static GraphView m_graphView;
		internal static bool m_sgHasFocus;
		private static bool m_loadVariables;

		// (Font needed to get string width for variable fields)
		private static Font m_loadedFont; // = EditorGUIUtility.LoadRequired("Fonts/Inter/Inter-Regular.ttf") as Font;

		private static void CheckForGraphs()
		{
			if (Time.realtimeSinceStartup < m_initTime + 3f)
			{
				return;
			}

			var focusedWindow = EditorWindow.focusedWindow;
			if (focusedWindow == null)
			{
				return;
			}

			if (focusedWindow.GetType().ToString().Contains("ShaderGraph"))
			{
				// is Shader Graph
				m_sgHasFocus = true;
				if (focusedWindow != m_prev || m_graphView == null)
				{
					m_sgWindow = focusedWindow;

					// Focused (new / different) Shader Graph window
					if (m_debugMessages)
					{
						Debug.Log("Switched Graph (variables cleared)");
					}

					m_graphView = GetGraphViewFromMaterialGraphEditWindow(focusedWindow);

					// Clear the stored variables and reload variables
					m_variableDict.Clear();
					m_variableNames.Clear();
					m_loadVariables = true;
					m_prev = focusedWindow;
				}

				if (m_graphView != null)
				{
					if (!m_disableVariableNodes)
					{
						UpdateVariableNodes();
					}

					if (!m_disableExtraFeatures)
					{
						ExtraFeatures.UpdateExtraFeatures();
					}

					m_loadVariables = false;
					//if (revalidateGraph) ValidateGraph();
				}
			}
			else
			{
				m_sgHasFocus = false;
			}
		}

		#region SGVariables

		private static readonly List<Port> m_editedPorts = new();

		private static void OnUndoRedo()
		{
			// Undo/Redo redraws the graph, so will cause "key already in use" errors.
			// Doing this will trigger the variables to be reloaded
			m_prev = null;
			m_initTime = Time.realtimeSinceStartup;
		}

		private static void UpdateVariableNodes()
		{
			HandlePortUpdates();
			
			m_graphView.nodes.ForEach(node =>
			{	
				if (node == null)
				{
					return;
				}

				if (node.title.Equals("Set"))
				{
					//node.
					SetNode(node);
				}
				else if (node.title.Equals("Get"))
				{
					GetNode(node);
				}
			});
		}
		
		private static void SetNode(Node node)
		{
			var field = TryGetTextField(node);
			if (field == null)
			{
				// 'Set' Setup (called once)
				field = CreateTextField(node, out var variableName);
				field.style.marginLeft = 25;
				field.style.marginRight = 4;
				if (!variableName.Equals(""))
				{
					// Register the node
					Register("", variableName, node);
				}

				field.RegisterValueChangedCallback(x => Register(x.previousValue, x.newValue, node));

				// Setup Node Type (Vector/Float)
				var inputPorts = GetInputPorts(node);
				Port connectedOutput = null;
				foreach (var input in inputPorts.ToList())
				{
					connectedOutput = GetConnectedPort(input);
					if (connectedOutput != null)
					{
						break;
					}
				}

				var portType = NodePortType.Vector4;

				if (connectedOutput != null)
				{
					var type = GetPortType(connectedOutput);
					if (type.Contains("Vector1"))
					{
						portType = NodePortType.Float;
					}
					else if (type.Contains("Vector2"))
					{
						portType = NodePortType.Vector2;
					}
					else if (type.Contains("Vector3"))
					{
						portType = NodePortType.Vector3;
					}
					else if (type.Contains("DynamicVector") || type.Contains("DynamicValue"))
					{
						// Handles output slots that can change between vector types (or vector/matrix types)
						// e.g. Most math based nodes use DynamicVector. Multiply uses DynamicValue
						var materialSlot = GetMaterialSlot(connectedOutput);
						var dynamicTypeField = materialSlot?.GetType().GetField("m_ConcreteValueType", bindingFlags);
						var typeString = dynamicTypeField?.GetValue(materialSlot).ToString() ?? string.Empty;
						
						if (typeString.Equals("Vector1"))
						{
							portType = NodePortType.Float;
						}
						else if (typeString.Equals("Vector2"))
						{
							portType = NodePortType.Vector2;
						}
						else if (typeString.Equals("Vector3"))
						{
							portType = NodePortType.Vector3;
						}
						else
						{
							portType = NodePortType.Vector4;
						}
					}
					// same as some later code in HandlePortUpdates, should really refactor into it's own method

					// Hide all input ports & make sure they are connected (probably could just connect based on port type, but this is easier)
					foreach (var input in inputPorts.ToList())
					{
						HideInputPort(input);

						//DisconnectAllEdges(node, input); // avoid disconnecting... seems this causes errors in some sg/unity versions
						Connect(connectedOutput, input);
					}
				}

				// Set type (shows required port)
				SetNodePortType(node, portType);

				// Test for invalid connections
				var outputPorts = GetOutputPorts(node);
				foreach (var output in outputPorts.ToList())
				{
					var connectedInput = GetConnectedPort(output);
					if (connectedInput != null && !connectedInput.node.title.Equals("Get"))
					{
						DisconnectAllEdges(node, output);
					}
					
					// Not allowed to connect to the outputs of 'Set' node
					// (unless it's the 'Get' node, which is connected automatically)
					// This can happen if node was created while dragging an edge from an input port
				}

				// Register methods to port.OnConnect / port.OnDisconnect, (is internal so we use reflection)
				inputPorts.ForEach(port =>
				{
					RegisterPortDelegates(port, OnRegisterNodeInputPortConnected, OnRegisterNodeInputPortDisconnected);
				});
				// If this breaks, an alternative is to just check the ports each frame for different types
			}
			else
			{
				// 'Set' Update (called each frame)
				if (m_loadVariables)
				{
					Register("", field.value, node);
				}

				var inputPorts = GetInputPorts(node);
				var outputPorts = GetOutputPorts(node);
				var inputPort = GetActivePort(inputPorts);

				// Make edges invisible
				Action<Port> portAction = output =>
				{
					foreach (var edge in output.connections)
					{
						if (edge.input.node.title.Equals("Get"))
						{
							if (edge.visible && !m_debugDontHideEdges)
							{
								edge.visible = false;
							}
						}
					}
				};
				outputPorts.ForEach(portAction);

				// Make edges invisible (if not active input port)
				portAction = input =>
				{
					foreach (var edge in input.connections)
					{
						if (edge.input != inputPort)
						{
							if (edge.visible && !m_debugDontHideEdges)
							{
								edge.visible = false;
							}
						}
					}
				};
				inputPorts.ForEach(portAction);

				if (!node.expanded)
				{
					var hasPorts = node.RefreshPorts();
					if (!hasPorts)
					{
						HideElement(field);
					}
				}
				else
				{
					ShowElement(field);
				}
			}
		}

		private static void GetNode(Node node)
		{
		#if UNITY_2021_2_OR_NEWER
			var field = TryGetDropdownField(node);
		#else
			// Unity 2020 did not have DropdownField,
			// (and 2021.1 doesn't have DropdownField.choices)
			// so for these, keep using TextField instead
			TextField field = TryGetTextField(node);
			#endif
			if (field == null)
			{
				// 'Get' Setup (called once)
				#if UNITY_2021_2_OR_NEWER
				field = CreateDropDownField(node);
				#else
				field = CreateTextField(node, out var variableName);
				#endif
				field.style.marginLeft = 4;
				field.style.marginRight = 25;
				field.RegisterValueChangedCallback(x => Get(x.newValue, node));

				var outputPorts = GetOutputPorts(node);
				var outputVector = outputPorts.AtIndex(0);
				var outputFloat = outputPorts.AtIndex(1);
				var outputTexture = outputPorts.AtIndex(2);

				// If both output ports are visible, do setup :
				// (as 'Set' node may trigger it first)
				if (!IsPortHidden(outputVector) && !IsPortHidden(outputFloat) && !IsPortHidden(outputTexture))
				{
					var key = GetSerializedVariableKey(node);
					ResizeNodeToFitText(node, key);

					//Get(key, node); // causes errors in 2022
					// (I think due to the DisconnectAllInputs then reconnecting later when 'Set' node triggers linking...)

					key = key.Trim().ToUpper();
					if (m_variableDict.TryGetValue(key, out var varNode))
					{
						// Make sure 'Get' node matches 'Set' type
						// accounts for node being copied
						SetNodePortType(node, GetNodePortType(varNode));
					}
					else
					{
						SetNodePortType(node, NodePortType.Vector4); // default to vector (hides other ports)
					}
				}
				else if (m_loadVariables)
				{
					var key = GetSerializedVariableKey(node);
					ResizeNodeToFitText(node, key);
				}

				var connectedInputF = GetConnectedPort(outputFloat);
				var portType = GetNodePortType(node);
				if (connectedInputF != null && portType == NodePortType.Vector4)
				{
					// Something is connected to the Float port, when the type is Vector
					// This can happen if node was created while dragging an edge from an input port
					MoveAllOutputs(outputFloat, outputVector);
				}
			}
			else
			{
				// 'Get' Update (called each frame)
				if (!node.expanded)
				{
					var hasPorts = node.RefreshPorts();
					if (!hasPorts)
					{
						HideElement(field);
					}
				}
				else
				{
					#if UNITY_2021_2_OR_NEWER
					field.choices = m_variableNames;
					#endif
					ShowElement(field);
				}
			}
		}
		
		private static void HandlePortUpdates()
		{
			for (var i = m_editedPorts.Count - 1; i >= 0; i--)
			{
				var port = m_editedPorts[i];
				var node = port.node;
				if (node == null)
				{
					// Node has been deleted, ignore
					m_editedPorts.RemoveAt(i);
					continue;
				}

				var outputConnectedToInput = GetConnectedPort(port);
				if (outputConnectedToInput != null)
				{
					if (m_debugMessages)
					{
						Debug.Log(outputConnectedToInput.portName + " > " + port.portName);
					}

					var inputPorts = GetInputPorts(node);
					// Disconnect Inputs & Reconnect
					foreach (var input in inputPorts.ToList())
					{
						if (IsPortHidden(input))
						{
							//DisconnectAllEdges(node, input);
							Connect(outputConnectedToInput, input);
						}
					}
					// we avoid changing the active port to prevent infinite loop

					var connectedSlotType = GetPortType(outputConnectedToInput);
					var inputSlotType = GetPortType(port);

					if (connectedSlotType != inputSlotType)
					{
						if (m_debugMessages)
						{
							Debug.Log(connectedSlotType + " > " + inputSlotType);
						}

						var portType = NodePortType.Vector4;
						if (connectedSlotType.Contains("Vector1"))
						{
							portType = NodePortType.Float;
						}
						else if (connectedSlotType.Contains("Vector2"))
						{
							portType = NodePortType.Vector2;
						}
						else if (connectedSlotType.Contains("Vector3"))
						{
							portType = NodePortType.Vector3;
						}
						else if (connectedSlotType.Contains("DynamicVector") || connectedSlotType.Contains("DynamicValue"))
						{
							// Handles output slots that can change between vector types (or vector/matrix types)
							// e.g. Most math based nodes use DynamicVector. Multiply uses DynamicValue
							var materialSlot = GetMaterialSlot(outputConnectedToInput);
							var dynamicTypeField = materialSlot?.GetType().GetField("m_ConcreteValueType", bindingFlags);
							var typeString = dynamicTypeField?.GetValue(materialSlot).ToString() ?? "";
							if (typeString.Equals("Vector1"))
							{
								portType = NodePortType.Float;
							}
							else if (typeString.Equals("Vector2"))
							{
								portType = NodePortType.Vector2;
							}
							else if (typeString.Equals("Vector3"))
							{
								portType = NodePortType.Vector3;
							}
							else
							{
								portType = NodePortType.Vector4;
							}
						}
						/*
						- While this works, it introduces a problem where
							if we trigger the Dynamic port to change type by connecting to input ports
							(e.g. a Vector4 node into a Multiply already connected to 'Set')
							it doesn't trigger the port Connect/Disconnect so the type of the 'Set' isn't updated!
						*/

						SetNodePortType(node, portType);
					}
				}
				else
				{
					// Removed Port
					var inputPorts = GetInputPorts(node);
					inputPorts.ForEach(p =>
					{
						if (p != port)
						{
							// we avoid changing the active port to prevent infinite loop
							DisconnectAllEdges(node, p);
						}
					});

					// Default to Vector4 type
					SetNodePortType(node, NodePortType.Vector4);
				}

				m_editedPorts.RemoveAt(i);
			}
		}

		#endregion

		#region UIElements

		private static TextField TryGetTextField(Node node)
		{
			return node.ElementAt(1) as TextField;
		}

		private static TextField CreateTextField(Node node, out string variableName)
		{
			// 'Get' Name (saved in the node's "synonyms" field)
			variableName = GetSerializedVariableKey(node);

			// Setup Text Field 
			var field = new TextField
			{
				style =
				{
					position = Position.Absolute
				}
			};
			if (m_debugPutTextFieldAboveNode)
			{
				field.style.top = -35; // put field above (debug)
			}
			else
			{
				field.style.top = 39; // put field over first input/output port
			}

			field.StretchToParentWidth();
			// Note : Later we also adjust margins so it doesn't hide the required ports

			#if UNITY_2022_1_OR_NEWER
			var textInput = field.Q<TextElement>(); // TextInput -> TextElement
			#else
			var textInput = field.ElementAt(0); // TextField -> TextInput
			#endif

			textInput.style.fontSize = 25;
			textInput.style.unityTextAlign = TextAnchor.MiddleCenter;

			field.value = variableName;

			// Add TextField to node VisualElement
			// Note : This must match what's in TryGetTextField
			node.Insert(1, field);

			return field;
		}

		#if UNITY_2021_2_OR_NEWER
		private static DropdownField TryGetDropdownField(Node node)
		{
			return node.ElementAt(1) as DropdownField;
		}

		private static DropdownField CreateDropDownField(Node node)
		{
			// 'Get' Name (saved in the node's "synonyms" field)
			var variableName = GetSerializedVariableKey(node);

			// Setup Text Field 
			var field = new DropdownField
			{
				choices = m_variableNames,
				style =
				{
					position = Position.Absolute
				}
			};
			if (m_debugPutTextFieldAboveNode)
			{
				field.style.top = -35; // put field above (debug)
			}
			else
			{
				field.style.top = 39; // put field over first input/output port
			}

			field.style.height = 33;
			field.StretchToParentWidth();
			// Note : Later we also adjust margins so it doesn't hide the required ports

			//var dropdownInput = field.ElementAt(0).ElementAt(0); // DropdownField->VisualElement->PopupTextElement
			var dropdownInput = field.Q<TextElement>();

			dropdownInput.style.fontSize = 25;
			dropdownInput.style.unityTextAlign = TextAnchor.MiddleCenter;

			field.value = variableName;

			// Add DropdownField to node VisualElement
			// Note : This must match what's in TryGetDropdownField
			node.Insert(1, field);

			return field;
		}
		#endif

		private static void ResizeNodeToFitText(Node node, string s)
		{
			if (m_loadedFont == null)
			{
				m_loadedFont = EditorGUIUtility.LoadRequired("Fonts/Inter/Inter-Regular.ttf") as Font;
			}

			if (m_loadedFont == null)
			{
				//Debug.LogError("Seems font (Fonts/Inter/Inter-Regular.ttf) is null? Cannot get string width, defaulting to 250");
				node.style.minWidth = 250;
			}
			else
			{
				m_loadedFont.RequestCharactersInTexture(s);
				float width = 0;
				foreach (var c in s)
				{
					if (m_loadedFont.GetCharacterInfo(c, out var info))
					{
						width += info.glyphWidth + info.advance;
					}
				}

				node.style.minWidth = width + 42; // margins/padding
			}

			node.MarkDirtyRepaint();
			//Debug.Log("ResizeNodeToFitText : " + width + ", string : " + s);
		}

		private static bool IsPortHidden(Port port)
		{
			return port.style.display == DisplayStyle.None || port.parent.style.display == DisplayStyle.None;
		}

		private static void HideInputPort(Port port)
		{
			// The SubGraph input ports have an additional element grouped for when the input is empty, that shows the (0,0,0,0) thing
			HideElement(port.parent);
		}

		private static void HideOutputPort(Port port)
		{
			HideElement(port);
		}

		private static void ShowInputPort(Port port)
		{
			ShowElement(port.parent);
		}

		private static void ShowOutputPort(Port port)
		{
			ShowElement(port);
		}

		private static void HideElement(VisualElement visualElement)
		{
			visualElement.style.display = DisplayStyle.None;
		}

		private static void ShowElement(VisualElement visualElement)
		{
			visualElement.style.display = DisplayStyle.Flex;
		}

		#endregion

		#region Get Input/Output Ports

		// Register/'Get' nodes support these types, should match port order
		private enum NodePortType
		{
			Vector4, // also DynamicVector, DynamicValue
			Float,
			Vector2,
			Vector3,
		}

		public static UQueryState<Port> GetInputPorts(Node node)
		{
			// As a small optimisation (hopefully), we're storing the UQueryState<Port> in userData
			// (maybe a List<Node> would be better?)
			var userData = node.inputContainer.userData;
			if (userData == null)
			{
				if (m_debugMessages)
				{
					Debug.Log("Setup Input Ports, " + node.title + " / " + GetSerializedVariableKey(node));
				}

				var inputPorts = node.inputContainer.Query<Port>().Build();
				node.inputContainer.userData = inputPorts;
				inputPorts.ForEach(port => { port.userData = GetMaterialSlotTypeReflection(port); });
				return inputPorts;
			}

			return (UQueryState<Port>)userData;
		}

		private static UQueryState<Port> GetOutputPorts(Node node)
		{
			// As a small optimisation (hopefully), we're storing the UQueryState<Port> in userData
			// (maybe a List<Node> would be better?)
			var userData = node.outputContainer.userData;
			if (userData == null)
			{
				if (m_debugMessages)
				{
					Debug.Log("Setup Output Ports, " + node.title + " / " + GetSerializedVariableKey(node));
				}

				var outputPorts = node.outputContainer.Query<Port>().Build();
				node.outputContainer.userData = outputPorts;
				outputPorts.ForEach(port => { port.userData = GetMaterialSlotTypeReflection(port); });
				return outputPorts;
			}

			return (UQueryState<Port>)userData;
		}

		private static Port GetActivePort(UQueryState<Port> ports)
		{
			// var portsList = ports.ToList();
			return ports.FirstOrDefault(p => !IsPortHidden(p));
		}

		/// <summary>
		/// Get the port connected to this port.
		/// If an input port is passed in, there should only be one connected (or zero, in which case this returns null).
		/// If an output port is passed in, the other port in the first connection is returned (or again, null if no connections).
		/// If you need to check every connection for the output port, use "foreach (Edge edge in port.connections){...}" instead.
		/// </summary>
		public static Port GetConnectedPort(Port port)
		{
			foreach (var edge in port.connections)
			{
				if (edge.parent == null)
				{
					// ignore any "broken" edges (shouldn't happen anymore (see Connect function), but just to be safe)
					continue;
				}

				var input = edge.input;
				var output = edge.output;
				return output == port ? input : output;
			}

			return null;
		}

		/// <summary>
		/// Returs a string of the Type of SG MaterialSlot the port uses (e.g. "UnityEditor.ShaderGraph.Vector1MaterialSlot")
		/// </summary>
		private static string GetPortType(Port port)
		{
			var type = (string)port.userData;
			if (type == null)
			{
				// Cache in userData so next time if the port is used we don't need to bother obtaining it again
				// (though note this will reset if an undo occurs)
				type = GetMaterialSlotTypeReflection(port);
				port.userData = type;
			}

			return type;
		}

		private static NodePortType GetNodePortType(Node node)
		{
			var isRegisterNode = node.title.Equals("Set");

			var inputPorts = GetInputPorts(node);
			var outputPorts = GetOutputPorts(node);

			var currentPortType = NodePortType.Vector4;

			if (isRegisterNode)
			{
				var inputVector = inputPorts.AtIndex(0);
				var inputFloat = inputPorts.AtIndex(1);
				var inputVector2 = inputPorts.AtIndex(2);
				var inputVector3 = inputPorts.AtIndex(3);
				
				if (!IsPortHidden(inputVector))
				{
					currentPortType = NodePortType.Vector4;
				}
				else if (!IsPortHidden(inputFloat))
				{
					currentPortType = NodePortType.Float;
				}
				else if (!IsPortHidden(inputVector2))
				{
					currentPortType = NodePortType.Vector2;
				}
				else if (!IsPortHidden(inputVector3))
				{
					currentPortType = NodePortType.Vector3;
				}
			}
			else
			{
				var outputVector = outputPorts.AtIndex(0);
				var outputFloat = outputPorts.AtIndex(1);
				var outputVector2 = outputPorts.AtIndex(2);
				var outputVector3 = outputPorts.AtIndex(3);
				
				if (!IsPortHidden(outputVector))
				{
					currentPortType = NodePortType.Vector4;
				}
				else if (!IsPortHidden(outputFloat))
				{
					currentPortType = NodePortType.Float;
				}
				else if (!IsPortHidden(outputVector2))
				{
					currentPortType = NodePortType.Vector2;
				}
				else if (!IsPortHidden(outputVector3))
				{
					currentPortType = NodePortType.Vector3;
				}
			}

			return currentPortType;
		}

		private static void SetNodePortType(Node node, NodePortType portType)
		{
			var isSetNode = node.title.Equals("Set");

			var inputPorts = GetInputPorts(node);
			var outputPorts = GetOutputPorts(node);

			var currentPortType = GetNodePortType(node);
			var typeChanged = currentPortType != portType;

			var inputVector = inputPorts.AtIndex(0);
			var inputFloat = inputPorts.AtIndex(1);

			
			var outputVector = outputPorts.AtIndex(0);
			var outputFloat = outputPorts.AtIndex(1);

			// Hide Ports
			HideInputPort(inputVector);
			HideInputPort(inputFloat);
			
			HideOutputPort(outputVector);
			HideOutputPort(outputFloat);
			
			if (isSetNode)
			{
				var inputVector2 = inputPorts.AtIndex(2);
				var inputVector3 = inputPorts.AtIndex(3);
				
				HideInputPort(inputVector2);
				HideInputPort(inputVector3);
			}
			else
			{
				var outputVector2 = outputPorts.AtIndex(2);
				var outputVector3 = outputPorts.AtIndex(3);
				
				HideOutputPort(outputVector2);
				HideOutputPort(outputVector3);
			}

			// Show Ports
			Port newOutput = null;
			if (portType == NodePortType.Vector4)
			{
				if (isSetNode)
				{
					ShowInputPort(inputVector);
				}
				else
				{
					ShowOutputPort(newOutput = outputVector);
				}
			}
			else if (portType == NodePortType.Float)
			{
				if (isSetNode)
				{
					ShowInputPort(inputFloat);
				}
				else
				{
					ShowOutputPort(newOutput = outputFloat);
				}
			}
			else if (portType == NodePortType.Vector2)
			{
				if (isSetNode)
				{
					var inputVector2 = inputPorts.AtIndex(2);
					ShowInputPort(inputVector2);
				}
				else
				{
					var outputVector2 = outputPorts.AtIndex(2);
					ShowOutputPort(newOutput = outputVector2);
				}
			}
			else if (portType == NodePortType.Vector3)
			{
				if (isSetNode)
				{
					var inputVector3 = inputPorts.AtIndex(3);
					ShowInputPort(inputVector3);
				}
				else
				{
					var outputVector3 = outputPorts.AtIndex(3);
					ShowOutputPort(newOutput = outputVector3);
				}
			}

			// move outputs to "active" port
			if (!isSetNode && typeChanged && newOutput != null)
			{
				Port currentOutput;
				if (currentPortType == NodePortType.Float)
				{
					currentOutput = outputFloat;
				}
				else if (currentPortType == NodePortType.Vector4)
				{
					currentOutput = outputVector;
				}
				else if (currentPortType == NodePortType.Vector2)
				{
					currentOutput = outputPorts.AtIndex(2);
				}
				else if (currentPortType == NodePortType.Vector3)
				{
					currentOutput = outputPorts.AtIndex(3);
				}
				else
				{
					currentOutput = outputPorts.AtIndex(2);
				}
				

				//MoveAllOutputs(node, currentOutput, newOutput);
				MoveAllOutputs(currentOutput, newOutput);
			}

			if (isSetNode && typeChanged)
			{
				// Relink to 'Get' nodes
				var nodes = LinkToAllGetVariableNodes(GetSerializedVariableKey(node).ToUpper(), node);
				foreach (var n in nodes)
				{
					SetNodePortType(n, portType);
				}
			}
		}

		private static void OnRegisterNodeInputPortConnected(Port port)
		{
			if (IsPortHidden(port))
			{
				return; // If hidden, ignore connections (also used to prevent infinite loop)
			}

			if (m_debugMessages)
			{
				Debug.Log("OnRegisterNodeInputPort Connected (" + port.portName + ")");
			}

			// Sadly it seems we can't edit connections directly here,
			// It errors as a collection is modified while SG is looping over it.
			// To avoid this, we'll assign the port to a list and check it during the EditorApplication.update
			// Note however this delayed action causes the edge to glitch out a bit.
			m_editedPorts.Add(port);
		}

		private static void OnRegisterNodeInputPortDisconnected(Port port)
		{
			if (IsPortHidden(port))
			{
				return; // If hidden, ignore connections (also used to prevent infinite loop)
			}

			if (m_debugMessages)
			{
				Debug.Log("OnRegisterNodeInputPort Disconnected (" + port.portName + ")");
			}

			//DisconnectAllInputs(port.node);
			m_editedPorts.Add(port);
		}

		#endregion

		#region Register/Get Variables

		private static readonly Dictionary<string, Node> m_variableDict = new();

		private static readonly List<string> m_variableNames = new();
		/*
		variableDict keys are always upper case
		variableNames stores the keys exactly as typed (but extra whitespace trimmed) - (only really used in 2021.2+ for dropdownfield)
		*/

		/// <summary>
		/// Adds the (newValue, node) to variables dictionary. Removes previousValue, if editing the correct node.
		/// </summary>
		private static void Register(string previousValue, string newValue, Node node)
		{
			ResizeNodeToFitText(node, newValue);

			previousValue = previousValue.Trim();
			newValue = newValue.Trim();
			Debug.Log(newValue);

			// dictionary keys (always upper case)
			var previousKey = previousValue.ToUpper();
			var newKey = newValue.ToUpper();

			var HadPreviousKey = !previousKey.Equals("");
			var HasNewKey = !newKey.Equals("");

			// Remove previous key from Dictionary (if it's the correct node as stored)
			Node n;
			if (HadPreviousKey)
			{
				if (m_variableDict.TryGetValue(previousKey, out n))
				{
					if (n == node)
					{
						if (m_debugMessages)
						{
							Debug.Log("Removed " + previousKey);
						}

						m_variableDict.Remove(previousKey);
						m_variableNames.Remove(previousValue);
					}
					else
					{
						if (m_debugMessages)
						{
							Debug.Log("Not same node, not removing key");
						}
					}
				}
			}

			if (m_variableDict.TryGetValue(newKey, out n))
			{
				// Already contains key, was is the same node? (Changing case, e.g. "a" to "A" triggers this still)
				if (node == n)
				{
					// Same node. Serialise the new value and return,
					SetSerializedVariableKey(node, newValue);
					return;
				}

				if (n == null || n.userData == null)
				{
					// Occurs if previous 'Set' node was deleted
					if (m_debugMessages)
					{
						Debug.Log("Replaced Null");
					}

					m_variableDict.Remove(newKey);
					m_variableNames.Remove(newValue);
				}
				else
				{
					if (m_debugMessages)
					{
						Debug.Log("Attempted to Register " + newKey + " but it's already in use!");
					}

					ShowValidationError(node, "Variable Key is already in use!");

					SetSerializedVariableKey(node, "");
					return;
				}
			}
			else
			{
				ClearErrorsForNode(node);
			}

			// Add new key to Dictionary
			if (HasNewKey)
			{
				if (m_debugMessages)
				{
					Debug.Log("Register " + newKey);
				}

				m_variableDict.Add(newKey, node);
				m_variableNames.Add(newValue);
			}

			// Allow key to be serialised (as user typed, not upper-case version)
			SetSerializedVariableKey(node, newValue);

			var outputPorts = GetOutputPorts(node);
			//if (HadPreviousKey) {
			// Key has changed. Update key on connected "Get Variable" nodes
			var outputPort = outputPorts.AtIndex(0); // (doesn't matter which port we use, as all should be connected)
			foreach (var edge in outputPort.connections)
			{
				if (edge.input != null && edge.input.node != null)
				{
					#if UNITY_2021_2_OR_NEWER
					var field = TryGetDropdownField(edge.input.node);
					#else
							// Unity 2020 did not have DropdownField,
							// (and 2021.1 doesn't have DropdownField.choices)
							// so for these, keep using TextField instead
							TextField field = TryGetTextField(edge.input.node);
					#endif
					field?.SetValueWithoutNotify(newValue);
					ResizeNodeToFitText(edge.input.node, newValue);
					SetSerializedVariableKey(edge.input.node, newValue);
				}
			}

			// OLD Behaviour : Disconnect
			/*
			// As the value has changed, disconnect any output edges
			// But first, change 'Get' node types back to Vector4 default
			Port outputPort = outputPorts.AtIndex(0); // (doesn't matter which port we use, as all should be connected)
			foreach (Edge edge in outputPort.connections) {
				if (edge.input != null && edge.input.node != null) {
					SetNodePortType(edge.input.node, NodePortType.Vector4);
				}
			}
			DisconnectAllOutputs(node);
			*/
			//}

			// Check if any 'Get Variable' nodes are using the key and connect them (if not already)
			if (HasNewKey)
			{
				var portType = GetNodePortType(node);
				var nodes = LinkToAllGetVariableNodes(newKey, node);
				foreach (var n2 in nodes)
				{
					SetNodePortType(n2, portType); // outputPort
				}
			}
		}

		/// <summary>
		/// Used on a 'Set' node to link it to all 'Get' nodes in the graph.
		/// </summary>
		private static List<Node> LinkToAllGetVariableNodes(string key, Node registerNode)
		{
			if (m_debugMessages)
			{
				Debug.Log("LinkToAllGetVariableNodes(" + key + ")");
			}

			var linkedNodes = new List<Node>();

			m_graphView.nodes.ForEach(NodeAction);
			//revalidateGraph = true;
			return linkedNodes;

			void NodeAction(Node n)
			{
				if (n.title.Equals("Get"))
				{
					var key2 = GetSerializedVariableKey(n).ToUpper();
					if (key == key2)
					{
						LinkRegisterToGetVariableNode(registerNode, n);
						linkedNodes.Add(n);
					}
				}
			}
		}

		/// <summary>
		/// Links each output port on the 'Set' node to the input on the 'Get' node (does not ValidateGraph, call manually)
		/// </summary>
		private static void LinkRegisterToGetVariableNode(Node registerNode, Node getNode)
		{
			//if (debugMessages) Debug.Log("Linked Register -> Get");
			var outputPorts = GetOutputPorts(registerNode);
			var inputPorts = GetInputPorts(getNode);
			var portCount = 2;
			// If ports change this needs updating.
			// This assumes the SubGraphs always have the same number of input/output ports,
			// and the order on both nodes is matching types to allow connections
			for (var i = 0; i < portCount; i++)
			{
				var outputPort = outputPorts.AtIndex(i);
				var inputPort = inputPorts.AtIndex(i);
				Connect(outputPort, inputPort, true);
			}
		}

		/// <summary>
		/// Gets the variable from the variables dictionary and links the 'Get' node to the stored 'Set' node.
		/// </summary>
		private static void Get(string key, Node node)
		{
			ResizeNodeToFitText(node, key);
			key = key.Trim();

			// Allow key to be serialised
			SetSerializedVariableKey(node, key);

			// Dictionary always uses upper-case version of key
			key = key.ToUpper();

			if (m_debugMessages)
			{
				Debug.Log("Get " + key);
			}

			if (m_variableDict.TryGetValue(key, out var varNode))
			{
				//var outputPorts = GetOutputPorts(varNode);
				//var inputPorts = GetInputPorts(node);

				// Make sure 'Get' node matches 'Set' type
				SetNodePortType(node, GetNodePortType(varNode));

				// Link, 'Set' > Get Variable
				DisconnectAllInputs(node);
				LinkRegisterToGetVariableNode(varNode, node);
				//revalidateGraph = true;
			}
			else
			{
				// Key doesn't exist. If any inputs, disconnect them
				DisconnectAllInputs(node);

				// Default to Vector4 input
				SetNodePortType(node, NodePortType.Vector4);
			}
		}

		#endregion

		#region Connect/Disconnect Edges

		/// <summary>
		/// Connects two ports with an edge
		/// </summary>
		public static Edge Connect(Port a, Port b, bool noValidate = false)
		{
			foreach (var bEdge in b.connections)
			{
				foreach (var aEdge in a.connections)
				{
					if (aEdge == bEdge)
					{
						// Nodes are already connected!
						return aEdge;
					}
				}
			}

			// This connects the ports *visually*, but SG seems to handle this later
			// so using this ends up creating a duplicate edge which we don't want
			//Edge edge = a.ConnectTo(b);

			// But the Reflection method needs an edge passed in, so we'll just create a dummy one I guess?
			var edge = new Edge()
			{
				output = a,
				input = b
			};

			// This connects the ports in terms of the Shader Graph Data
			var sgEdge = ConnectReflection(edge, noValidate);
			if (sgEdge == null)
			{
				// Oh no, something went wrong!
				if (m_debugMessages)
				{
					Debug.LogWarning("sgEdge was null! (This is bad as it'll break copying)");
				}
				// This can cause an error here when trying to copy the node :
				// https://github.com/Unity-Technologies/Graphics/blob/3f3263397f0c880135b4f42d623f1510a153e20e/com.unity.shadergraph/Editor/Util/CopyPasteGraph.cs#L149

				ShowValidationError(edge.input.node, "Failed to Get Variable! Did you create a loop?");
				// Preview may also be incorrect if 'Set' node is float type here
			}

			return edge;
		}

		/// <summary>
		/// Disconnects all edges from the specified node and port
		/// </summary>
		private static void DisconnectAllEdges(Node node, Port port)
		{
			// If already has no connections, don't bother continuing
			if (!port.connections.Any())
			{
				return;
			}

			/*
			foreach (Edge edge in port.connections) {
				if (port.direction == Direction.Input) {
					edge.output.Disconnect(edge);
				} else {
					edge.input.Disconnect(edge);
				}
			}
			// Unsure if I really need to disconnect the other end
			// Currently doesn't seem to matter that much so leaving it out
			// When we disconnect below in the reflection, SG triggers ValidateGraph
			// which triggers CleanupGraph and removes "orphan" edges anyway
			*/

			// This disconnects all connections in port *visually*
			port.DisconnectAll();

			int index;
			if (port.direction == Direction.Input)
			{
				// The SubGraph input ports have an additional element grouped
				// (for showing value when not connected, e.g. the (0,0,0,0) thing)
				var parent = port.parent;
				index = parent.parent.IndexOf(parent);
			}
			else
			{
				index = port.parent.IndexOf(port);
			}

			// This disconnects all connections in port in terms of the Shader Graph Data
			DisconnectAllReflection(node, index, port.direction);
		}

		/// <summary>
		/// Disconnects all edges in all input ports on node
		/// </summary>
		public static void DisconnectAllInputs(Node node)
		{
			var inputPorts = GetInputPorts(node);
			inputPorts.ForEach(port => { DisconnectAllEdges(node, port); });
		}

		/// <summary>
		/// Disconnects all edges in all output ports on node
		/// </summary>
		public static void DisconnectAllOutputs(Node node)
		{
			var outputPorts = GetOutputPorts(node);
			outputPorts.ForEach(port => { DisconnectAllEdges(node, port); });
		}

		/// <summary>
		/// Moves all outputs on node to a different port on the same node (though might work if toPort is on a different node too?)
		/// </summary>
		//private static void MoveAllOutputs(Node node, Port port, Port toPort)
		private static void MoveAllOutputs(Port port, Port toPort)
		{
			// Move all connections from port to toPort (on same node)
			var portsToConnect = new List<Port>();
			foreach (var edge in port.connections)
			{
				var input = edge.input;
				portsToConnect.Add(input);
			}
			//DisconnectAllEdges(node, port); // Remove all edges from previous port
			// Seems we don't need to disconnect the edges, probably because we're connecting
			// to the same inputs which can only have 1 edge, so it gets overridden

			foreach (var portToConnect in portsToConnect)
			{
				Connect(toPort, portToConnect, true);
			}
			//revalidateGraph = true;
		}

		#endregion

		#region Reflection

		// This probably isn't pretty.

		public static object graphData;

		internal const BindingFlags bindingFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

		internal static Type graphDataType;
		internal static Type abstractMaterialNodeType;
		internal static Type colorNodeType;

		private static Type materialGraphEditWindowType;
		private static Type graphEditorViewType;
		private static Type materialSlotType;
		private static Type IEdgeType;
		private static Type listType_MaterialSlot;
		private static Type listType_GenericParam0;
		private static Type listType_IEdge;

		internal static FieldInfo synonymsField;

		private static FieldInfo graphEditorViewField;
		private static FieldInfo graphViewField;
		private static FieldInfo graphDataField;
		private static FieldInfo onConnectField;
		private static FieldInfo onDisconnectField;

		private static PropertyInfo shaderPortSlotProperty;
		private static PropertyInfo materialSlotReferenceProperty;
		private static PropertyInfo objectIdProperty;

		private static MethodInfo connectMethod;
		private static MethodInfo connectNoValidateMethod;
		private static MethodInfo getInputSlots_MaterialSlot;
		private static MethodInfo getOutputSlots_MaterialSlot;
		private static MethodInfo getEdges;
		private static MethodInfo removeEdge;
		private static MethodInfo validateGraph;
		private static MethodInfo addValidationError;
		private static MethodInfo clearErrorsForNode;
		private static MethodInfo removeNode;

		public static Assembly sgAssembly;

		private static void GetShaderGraphTypes()
		{
			sgAssembly = Assembly.Load(new AssemblyName("Unity.ShaderGraph.Editor"));

			materialGraphEditWindowType = sgAssembly.GetType("UnityEditor.ShaderGraph.Drawing.MaterialGraphEditWindow");
			abstractMaterialNodeType = sgAssembly.GetType("UnityEditor.ShaderGraph.AbstractMaterialNode");
			materialSlotType = sgAssembly.GetType("UnityEditor.ShaderGraph.MaterialSlot");
			IEdgeType = sgAssembly.GetType("UnityEditor.Graphing.IEdge");
			colorNodeType = sgAssembly.GetType("UnityEditor.ShaderGraph.ColorNode");
		}

		// window:  MaterialGraphEditWindow  member: m_GraphEditorView ->
		// VisualElement:  GraphEditorView   member: m_GraphView  ->
		// GraphView(VisualElement):  MaterialGraphView   
		private static GraphView GetGraphViewFromMaterialGraphEditWindow(EditorWindow win)
		{
			if (materialGraphEditWindowType == null)
			{
				GetShaderGraphTypes();
				if (materialGraphEditWindowType == null)
				{
					return null;
				}
			}

			if (graphEditorViewField == null)
			{
				graphEditorViewField = materialGraphEditWindowType.GetField("m_GraphEditorView", bindingFlags);
			}

			var graphEditorView = graphEditorViewField?.GetValue(win);
			if (graphEditorView == null)
			{
				return null;
			}
			
			if (graphEditorViewType == null)
			{
				graphEditorViewType = graphEditorView.GetType();
				graphViewField = graphEditorViewType.GetField("m_GraphView", bindingFlags);
				graphDataField = graphEditorViewType.GetField("m_Graph", bindingFlags);
			}

			// Get Graph View
			var graphView = (GraphView)graphViewField?.GetValue(graphEditorView);
			graphData = graphDataField?.GetValue(graphEditorView);
			graphDataType ??= graphData?.GetType();

			return graphView;
		}

		/// <summary>
		/// Converts from GraphView.Node (used for visuals) to the actual Shader Graph Node (a type that inherits AbstractMaterialNode)
		/// </summary>
		public static object NodeToSGMaterialNode(Node node)
		{
			// SG stores the Material Node in the userData of the VisualElement
			return node.userData;
		}

		/// <summary>
		/// Obtains the values stored in synonyms (serialized by SG). Input should be NodeSGMaterialNode(node)
		/// </summary>
		public static string[] GetSerializedValues(object materialNode)
		{
			// We store values in the node's "synonyms" field
			// Nodes usually use it for the Search Box so it can display "Float" even if the user types "Vector 1"
			// But it's also serialized in the actual Shader Graph file where it then isn't really used, so it's mine now!~
			if (synonymsField == null)
			{
				synonymsField = abstractMaterialNodeType.GetField("synonyms");
			}

			return (string[])synonymsField.GetValue(materialNode);
		}

		/// <summary>
		/// Sets the values stored in synonyms (serialized by SG). Input should be NodeSGMaterialNode(node)
		/// </summary>
		public static void SetSerializedValues(object materialNode, string[] values)
		{
			if (synonymsField == null)
			{
				synonymsField = abstractMaterialNodeType.GetField("synonyms");
			}

			synonymsField.SetValue(materialNode, values);
		}

		private static string GetSerializedVariableKey(Node node)
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

		private static void SetSerializedVariableKey(Node node, string key)
		{
			var materialNode = NodeToSGMaterialNode(node);
			SetSerializedValues(materialNode, new[] { key });
		}

		private static string GetMaterialSlotTypeReflection(Port port)
		{
			return GetMaterialSlot(port)?.GetType().ToString();
		}

		[CanBeNull]
		internal static object GetMaterialSlot(Port port)
		{
			// ShaderPort -> MaterialSlot "slot"
			if (shaderPortSlotProperty == null)
			{
				shaderPortSlotProperty = port.GetType().GetProperty("slot");
			}

			return shaderPortSlotProperty?.GetValue(port);
		}

		[CanBeNull]
		private static object GetSlotReference(object materialSlot)
		{
			// MaterialSlot -> SlotReference "slotReference"
			if (materialSlotReferenceProperty == null)
			{
				materialSlotReferenceProperty = materialSlot.GetType().GetProperty("slotReference");
			}
			
			return materialSlotReferenceProperty?.GetValue(materialSlot);
		}

		private static object ConnectReflection(Edge edge, bool noValidate)
		{
			// GraphData.Connect(SlotReference fromSlotRef, SlotReference toSlotRef)
			if (connectMethod == null)
			{
				connectMethod = graphDataType.GetMethod("Connect");
			}

			if (connectNoValidateMethod == null)
			{
				connectNoValidateMethod = graphDataType.GetMethod("ConnectNoValidate", bindingFlags);
			}

			var method = noValidate ? connectNoValidateMethod : connectMethod;
			var parameters = method?.GetParameters().Length == 3
				? new[]
				{
					GetSlotReference(GetMaterialSlot(edge.output)),
					GetSlotReference(GetMaterialSlot(edge.input)),
					false
				}
				: new[]
				{
					GetSlotReference(GetMaterialSlot(edge.output)),
					GetSlotReference(GetMaterialSlot(edge.input))
				};
			
			var sgEdge = method?.Invoke(graphData, parameters);

			// Connect returns type of UnityEditor.Graphing.Edge
			// https://github.com/Unity-Technologies/Graphics/blob/master/com.unity.shadergraph/Editor/Data/Implementation/Edge.cs
			// It needs to be stored in the userData of the GraphView's Edge VisualElement (in order to support copying nodes)
			edge.userData = sgEdge;

			// Note, it can be null (which will cause an error when trying to copy it) :
			// if either slotRef.node is null
			// if both nodes belong to different graphs
			// if the outputNode is already connected to nodes connected after inputNode (prevents infinite loops)
			// if slot cannot be found in node using slotRef.slotId
			// if both slots are outputs (strangely it doesn't seem to check for both being inputs?)
			return sgEdge;
		}

		private static void DisconnectAllReflection(Node node, int portIndex, Direction direction)
		{
			var abstractMaterialNode = NodeToSGMaterialNode(node);

			// This all feels pretty hacky, but it works~
			#if UNITY_2021_2_OR_NEWER
			// Reflection for : AbstractMaterialNode.GetInputSlots(List<MaterialSlot> list) / GetOutputSlots(List<MaterialSlot> list)
			listType_GenericParam0 ??= typeof(List<>).MakeGenericType(Type.MakeGenericMethodParameter(0));

			if (getInputSlots_MaterialSlot == null)
			{
				var getInputSlots = abstractMaterialNodeType.GetMethod("GetInputSlots", new[] { listType_GenericParam0 });
				getInputSlots_MaterialSlot = getInputSlots?.MakeGenericMethod(materialSlotType);
			}

			if (getOutputSlots_MaterialSlot == null)
			{
				var getOutputSlots = abstractMaterialNodeType.GetMethod("GetOutputSlots", new[] { listType_GenericParam0 });
				getOutputSlots_MaterialSlot = getOutputSlots?.MakeGenericMethod(materialSlotType);
			}
			#else
			if (getInputSlots_MaterialSlot == null) 
			{
				var getInputSlots = abstractMaterialNodeType.GetMethod("GetInputSlots");
				getInputSlots_MaterialSlot = getInputSlots?.MakeGenericMethod(materialSlotType);
			}
			if (getOutputSlots_MaterialSlot == null) 
			{
				var getOutputSlots = abstractMaterialNodeType.GetMethod("GetOutputSlots");
				getOutputSlots_MaterialSlot = getOutputSlots?.MakeGenericMethod(materialSlotType);
			}
			#endif

			
			listType_MaterialSlot ??= typeof(List<>).MakeGenericType(materialSlotType);

			var materialSlotList = (IList)Activator.CreateInstance(listType_MaterialSlot);
			var method = direction == Direction.Input ? getInputSlots_MaterialSlot : getOutputSlots_MaterialSlot;
			method?.Invoke(abstractMaterialNode, new object[] { materialSlotList });

			var slot = materialSlotList[portIndex]; // Type : (MaterialSlot)
			var slotReference = GetSlotReference(slot);

			// Reflection for : graphData.GetEdges(SlotReference slot, List<IEdge> list)
			listType_IEdge ??= typeof(List<>).MakeGenericType(IEdgeType);

			if (getEdges == null)
			{
				getEdges = graphDataType.GetMethod("GetEdges", new[] { slotReference?.GetType(), listType_IEdge });
			}

			var edgeList = (IList)Activator.CreateInstance(listType_IEdge);
			getEdges?.Invoke(graphData, new[] { slotReference, edgeList });

			// For each edge, remove it!
			// Reflection for : graphData.RemoveEdge(IEdge edge)
			// Note : changed to RemoveEdgeNoValidate so it doesn't try to ValidateGraph for every removed edge
			// RemoveEdgeNoValidate(IEdge e, bool reevaluateActivity = true)
			if (removeEdge == null)
			{
				removeEdge = graphDataType.GetMethod("RemoveEdgeNoValidate", bindingFlags);
			}

			foreach (var edge in edgeList)
			{
				removeEdge.Invoke(graphData, new[] { edge, true });
			}
		}
		
		/// <summary>
		/// Registers to the port's OnConnect and OnDisconnect delegates (via Reflection as they are internal)
		/// </summary>
		private static void RegisterPortDelegates(Port port, Action<Port> OnConnect, Action<Port> OnDisconnect)
		{
			// internal Action<Port> OnConnect / OnDisconnect;
			if (onConnectField == null)
			{
				onConnectField = typeof(Port).GetField("OnConnect", bindingFlags);
			}

			if (onDisconnectField == null)
			{
				onDisconnectField = typeof(Port).GetField("OnDisconnect", bindingFlags);
			}

			var onConnect = (Action<Port>)onConnectField?.GetValue(port);
			var onDisconnect = (Action<Port>)onDisconnectField?.GetValue(port);

			// OnRegisterNodeInputPortConnected, OnRegisterNodeInputPortDisconnected
			onConnectField?.SetValue(port, onConnect + OnConnect);
			onDisconnectField?.SetValue(port, onDisconnect + OnDisconnect);
		}

		private static void ShowValidationError(Node node, string text)
		{
			if (objectIdProperty == null)
			{
				objectIdProperty = abstractMaterialNodeType.GetProperty("objectId", bindingFlags);
			}
			if (addValidationError == null)
			{
				addValidationError = graphDataType.GetMethod("AddValidationError");
			}

			var materialNode = NodeToSGMaterialNode(node);
			var objectId = objectIdProperty?.GetValue(materialNode);
			addValidationError?.Invoke(graphData, new[]
			{
				objectId, text, ShaderCompilerMessageSeverity.Error
			});
		}

		private static void ClearErrorsForNode(Node node)
		{
			if (clearErrorsForNode == null)
			{
				clearErrorsForNode = graphDataType.GetMethod("ClearErrorsForNode");
			}
			
			var materialNode = NodeToSGMaterialNode(node);
			clearErrorsForNode?.Invoke(graphData, new[] { materialNode });
		}

		public static void RemoveNode(Node node)
		{
			if (removeNode == null)
			{
				removeNode = graphDataType.GetMethod("RemoveNode", bindingFlags);
			}
			
			removeNode?.Invoke(graphData, new[] { NodeToSGMaterialNode(node) });
		}

		#endregion
	}
}