using NodeEditor;
using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.IO;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;
using UAssetAPI.Kismet.Bytecode;
using UAssetAPI.Kismet.Bytecode.Expressions;

namespace UAssetGUI
{

    public class KismetEditor : Panel
    {
        public NodesControl NodeEditor;

        public KismetEditor()
        {
            AutoScroll = true;

            NodeEditor = new NodesControl()
            {
                Visible = true,
                //AutoSize = true,
                //Multiline = true,
                //ReadOnly = true,
                //BackgroundImage = Properties.Resources.grid,
                Location = new System.Drawing.Point(0, 0),
                Name = "nodesControl",
                Size = new System.Drawing.Size(5000, 5000),
                TabIndex = 0,
                Context = null,
            };

            Controls.Add(NodeEditor);

            // needed else panel content won't update until scroll end
            Scroll += (sender, e) => {
                switch (e.ScrollOrientation)
                {
                    case ScrollOrientation.VerticalScroll:
                        VerticalScroll.Value = e.NewValue;
                        break;
                    case ScrollOrientation.HorizontalScroll:
                        HorizontalScroll.Value = e.NewValue;
                        break;
                }
            };
        }


        public class Value{}
        static Parameter PinExecute = new Parameter { Name = "execute", Direction = Direction.In, ParameterType = typeof(ExecutionPath) };
        static Parameter PinThen = new Parameter { Name = "then", Direction = Direction.Out, ParameterType = typeof(ExecutionPath) };
        static Parameter PinInValue = new Parameter { Name = "in", Direction = Direction.In, ParameterType = typeof(Value) };
        static Parameter PinOutValue = new Parameter { Name = "out", Direction = Direction.Out, ParameterType = typeof(Value) };

        internal struct JumpConnection
        {
            internal NodeVisual OutputNode;
            internal string OutputPin;
            internal uint InputIndex;
        }

        public void SetBytecode(KismetExpression[] bytecode)
        {
            NodeEditor.graph.Nodes.Clear();
            NodeEditor.graph.Connections.Clear();

            int startX = 200;
            int startY = 200;
            int posX = startX;
            int posY = startY;
            int stepX = 200;
            int stepY = 200;
            int maxX = 1000;
            int maxY = 1000;

            var offsets = GetOffsets(bytecode).ToDictionary(l => l.Item1, l => l.Item2);
            var nodeMap = new Dictionary<KismetExpression, NodeVisual>();


            var jumpConnections = new List<JumpConnection>();

            NodeVisual BuildExecNode(uint index, KismetExpression ex)
            {
                NodeVisual node;
                if (nodeMap.TryGetValue(ex, out node))
                    return node;

                var name = ex.GetType().Name;

                var type = new CustomNodeType
                {
                    Name = ex.GetType().Name,
                    Parameters = new List<Parameter>{},
                };

                void add_exec()
                {
                    type.Parameters.Add(PinExecute);
                    type.Parameters.Add(PinThen);
                }

                node = new NodeVisual()
                {
                    X = posX,
                    Y = posY,
                    Type = type,
                    Callable = false,
                    ExecInit = false,
                    Name = $"{index}: {name}",
                    Order = NodeEditor.graph.Nodes.Count,
                };

                switch (ex)
                {
                    case EX_Return:
                    case EX_EndOfScript:
                    case EX_PopExecutionFlow:
                    case EX_ComputedJump:
                        type.Parameters.Add(PinExecute);
                        break;
                    case EX_Jump e:
                        add_exec();
                        jumpConnections.Add(new JumpConnection { OutputNode = node, OutputPin = "then", InputIndex = e.CodeOffset });
                        break;
                    case EX_JumpIfNot e:
                        add_exec();
                        type.Parameters.Add(new Parameter { Name = "else", Direction = Direction.Out, ParameterType = typeof(ExecutionPath) });
                        type.Parameters.Add(new Parameter { Name = "condition", Direction = Direction.In, ParameterType = typeof(bool) });

                        jumpConnections.Add(new JumpConnection { OutputNode = node, OutputPin = "then", InputIndex = index + GetSize(ex) });
                        jumpConnections.Add(new JumpConnection { OutputNode = node, OutputPin = "else", InputIndex = e.CodeOffset });
                        break;
                    case EX_PushExecutionFlow e:
                        type.Parameters.Add(PinExecute);
                        type.Parameters.Add(new Parameter { Name = "first", Direction = Direction.Out, ParameterType = typeof(ExecutionPath) });
                        type.Parameters.Add(new Parameter { Name = "then", Direction = Direction.Out, ParameterType = typeof(ExecutionPath) });

                        jumpConnections.Add(new JumpConnection { OutputNode = node, OutputPin = "first", InputIndex = index + GetSize(ex) });
                        jumpConnections.Add(new JumpConnection { OutputNode = node, OutputPin = "then", InputIndex = e.PushingAddress });
                        break;
                    case EX_PopExecutionFlowIfNot e:
                        add_exec();
                        type.Parameters.Add(new Parameter { Name = "condition", Direction = Direction.In, ParameterType = typeof(bool) });
                        break;
                    default:
                        add_exec();
                        jumpConnections.Add(new JumpConnection { OutputNode = node, OutputPin = "then", InputIndex = index + GetSize(ex) });
                        break;
                };

                nodeMap.Add(ex, node);
                return node;
            }

            uint index = 0;
            foreach (var ex in bytecode)
            {
                var node = BuildExecNode(index, ex);
                node.X = posX;
                node.Y = posY;
                maxX = Math.Max(maxX, (int) node.X);
                maxY = Math.Max(maxY, (int) node.Y);
                NodeEditor.graph.Nodes.Add(node);

                posX += stepX;

                Console.WriteLine($"{index}: {ex}");
                index += GetSize(ex);
            }
            foreach (var jump in jumpConnections)
            {
                KismetExpression ex;
                if (!offsets.TryGetValue(jump.InputIndex, out ex))
                {
                    Console.WriteLine($"could not find expression at {jump.InputIndex}");
                    continue;
                }
                NodeVisual node;
                if (!nodeMap.TryGetValue(ex, out node))
                {
                    Console.WriteLine($"could not find node at {jump.InputIndex}");
                    continue;
                }

                var conn = new NodeConnection
                {
                    OutputNode = jump.OutputNode,
                    OutputSocketName = jump.OutputPin,
                    InputNode = node,
                    InputSocketName = "execute",
                };
                //Console.WriteLine($"{jump.OutputNode} {node}");
                //Console.WriteLine($"{conn.OutputSocket} {conn.InputSocket}");
                NodeEditor.graph.Connections.Add(conn);
            }

            NodeEditor.Size = new System.Drawing.Size(maxX + stepX, maxY + stepY);

            NodeEditor.Refresh();
            NodeEditor.needRepaint = true;
        }

        public static IEnumerable<(uint, KismetExpression)> GetOffsets(KismetExpression[] bytecode) {
            var offsets = new List<(uint, KismetExpression)>();
            uint offset = 0;
            foreach (var inst in bytecode) {
                offsets.Add((offset, inst));
                offset += GetSize(inst);
            }
            return offsets;
        }

        public static void Walk(KismetExpression ex, Action<KismetExpression> func) {
            uint offset = 0;
            Walk(ref offset, ex, (e, o) => func(e));
        }

        public static void Walk(ref uint offset, KismetExpression ex, Action<KismetExpression, uint> func) {
            func(ex, offset);
            offset++;
            switch (ex) {
                case EX_FieldPathConst e:
                    Walk(ref offset, e.Value, func);
                    break;
                case EX_SoftObjectConst e:
                    Walk(ref offset, e.Value, func);
                    break;
                case EX_AddMulticastDelegate e:
                    Walk(ref offset, e.Delegate, func);
                    Walk(ref offset, e.DelegateToAdd, func);
                    break;
                case EX_ArrayConst e:
                    offset += 8;
                    foreach (var p in e.Elements) Walk(ref offset, p, func);
                    break;
                case EX_ArrayGetByRef e:
                    Walk(ref offset, e.ArrayVariable, func);
                    Walk(ref offset, e.ArrayIndex, func);
                    break;
                case EX_Assert e:
                    offset += 3;
                    Walk(ref offset, e.AssertExpression, func);
                    break;
                case EX_BindDelegate e:
                    offset += 12;
                    Walk(ref offset, e.Delegate, func);
                    Walk(ref offset, e.ObjectTerm, func);
                    break;
                case EX_CallMath e:
                    offset += 8;
                    foreach (var p in e.Parameters) Walk(ref offset, p, func);
                    offset += 1;
                    break;
                case EX_CallMulticastDelegate e:
                    offset += 8;
                    Walk(ref offset, e.Delegate, func);
                    foreach (var p in e.Parameters) Walk(ref offset, p, func);
                    offset += 1;
                    break;
                case EX_ClearMulticastDelegate e:
                    Walk(ref offset, e.DelegateToClear, func);
                    break;
                case EX_ComputedJump e:
                    Walk(ref offset, e.CodeOffsetExpression, func);
                    break;
                case EX_Context e: // +EX_Context_FailSilent +EX_ClassContext
                    Walk(ref offset, e.ObjectExpression, func);
                    offset += 12;
                    Walk(ref offset, e.ContextExpression, func);
                    break;
                case EX_CrossInterfaceCast e:
                    offset += 8;
                    Walk(ref offset, e.Target, func);
                    break;
                case EX_DynamicCast e:
                    offset += 8;
                    Walk(ref offset, e.TargetExpression, func);
                    break;
                case EX_FinalFunction e: // +EX_LocalFinalFunction
                    offset += 8;
                    foreach (var p in e.Parameters) Walk(ref offset, p, func);
                    offset += 1;
                    break;
                case EX_InterfaceContext e:
                    Walk(ref offset, e.InterfaceValue, func);
                    break;
                case EX_InterfaceToObjCast e:
                    offset += 8;
                    Walk(ref offset, e.Target, func);
                    break;
                case EX_JumpIfNot e:
                    offset += 4;
                    Walk(ref offset, e.BooleanExpression, func);
                    break;
                case EX_Let e:
                    offset += 8;
                    Walk(ref offset, e.Variable, func);
                    Walk(ref offset, e.Expression, func);
                    break;
                case EX_LetBool e:
                    Walk(ref offset, e.VariableExpression, func);
                    Walk(ref offset, e.AssignmentExpression, func);
                    break;
                case EX_LetDelegate e:
                    Walk(ref offset, e.VariableExpression, func);
                    Walk(ref offset, e.AssignmentExpression, func);
                    break;
                case EX_LetMulticastDelegate e:
                    Walk(ref offset, e.VariableExpression, func);
                    Walk(ref offset, e.AssignmentExpression, func);
                    break;
                case EX_LetObj e:
                    Walk(ref offset, e.VariableExpression, func);
                    Walk(ref offset, e.AssignmentExpression, func);
                    break;
                case EX_LetValueOnPersistentFrame e:
                    offset += 8;
                    Walk(ref offset, e.AssignmentExpression, func);
                    break;
                case EX_LetWeakObjPtr e:
                    Walk(ref offset, e.VariableExpression, func);
                    Walk(ref offset, e.AssignmentExpression, func);
                    break;
                case EX_VirtualFunction e: // +EX_LocalVirtualFunction
                    offset += 12;
                    foreach (var p in e.Parameters) Walk(ref offset, p, func);
                    offset += 1;
                    break;
                case EX_MapConst e:
                    offset += 20;
                    foreach (var p in e.Elements) Walk(ref offset, p, func);
                    break;
                case EX_MetaCast e:
                    offset += 8;
                    Walk(ref offset, e.TargetExpression, func);
                    break;
                case EX_ObjToInterfaceCast e:
                    offset += 8;
                    Walk(ref offset, e.Target, func);
                    break;
                case EX_PopExecutionFlowIfNot e:
                    Walk(ref offset, e.BooleanExpression, func);
                    break;
                case EX_PrimitiveCast e:
                    offset += 1;
                    Walk(ref offset, e.Target, func);
                    break;
                case EX_RemoveMulticastDelegate e:
                    Walk(ref offset, e.Delegate, func);
                    Walk(ref offset, e.DelegateToAdd, func);
                    break;
                case EX_Return e:
                    Walk(ref offset, e.ReturnExpression, func);
                    break;
                case EX_SetArray e:
                    Walk(ref offset, e.AssigningProperty, func);
                    foreach (var p in e.Elements) Walk(ref offset, p, func);
                    offset += 1;
                    break;
                case EX_SetConst e:
                    offset += 12;
                    foreach (var p in e.Elements) Walk(ref offset, p, func);
                    offset += 1;
                    break;
                case EX_SetMap e:
                    Walk(ref offset, e.MapProperty, func);
                    offset += 4;
                    foreach (var p in e.Elements) Walk(ref offset, p, func);
                    break;
                case EX_SetSet e:
                    Walk(ref offset, e.SetProperty, func);
                    offset += 4;
                    foreach (var p in e.Elements) Walk(ref offset, p, func);
                    break;
                case EX_Skip e:
                    offset += 4;
                    Walk(ref offset, e.SkipExpression, func);
                    break;
                case EX_StructConst e:
                    offset += 12;
                    foreach (var p in e.Value) Walk(ref offset, p, func);
                    offset += 1;
                    break;
                case EX_StructMemberContext e:
                    offset += 8;
                    Walk(ref offset, e.StructExpression, func);
                    break;
                case EX_SwitchValue e:
                    offset += 6;
                    Walk(ref offset, e.IndexTerm, func);
                    foreach (var p in e.Cases) {
                        Walk(ref offset, p.CaseIndexValueTerm, func);
                        offset += 4;
                        Walk(ref offset, p.CaseTerm, func);
                    }
                    Walk(ref offset, e.DefaultTerm, func);
                    break;
                default:
                    offset += GetSize(ex) - 1;
                    break;
            }
        }
        public static uint GetSize(KismetExpression exp)
        {
            return 1 + exp switch
            {
                EX_PushExecutionFlow => 4,
                EX_ComputedJump e => GetSize(e.CodeOffsetExpression),
                EX_Jump e => 4,
                EX_JumpIfNot e => 4 + GetSize(e.BooleanExpression),
                EX_LocalVariable e => 8,
                EX_DefaultVariable e => 8,
                EX_ObjToInterfaceCast e => 8 + GetSize(e.Target),
                EX_Let e => 8 + GetSize(e.Variable) + GetSize(e.Expression),
                EX_LetObj e => GetSize(e.VariableExpression) + GetSize(e.AssignmentExpression),
                EX_LetBool e => GetSize(e.VariableExpression) + GetSize(e.AssignmentExpression),
                EX_LetWeakObjPtr e => GetSize(e.VariableExpression) + GetSize(e.AssignmentExpression),
                EX_LetValueOnPersistentFrame e => 8 + GetSize(e.AssignmentExpression),
                EX_StructMemberContext e => 8 + GetSize(e.StructExpression),
                EX_MetaCast e => 8 + GetSize(e.TargetExpression),
                EX_DynamicCast e => 8 + GetSize(e.TargetExpression),
                EX_PrimitiveCast e => 1 + e.ConversionType switch { ECastToken.ObjectToInterface => 8U, /* TODO InterfaceClass */ _ => 0U} + GetSize(e.Target),
                EX_PopExecutionFlow e => 0,
                EX_PopExecutionFlowIfNot e => GetSize(e.BooleanExpression),
                EX_CallMath e => 8 + e.Parameters.Select(p => GetSize(p)).Aggregate(0U, (acc, x) => x + acc) + 1,
                EX_SwitchValue e => 6 + GetSize(e.IndexTerm) + e.Cases.Select(c => GetSize(c.CaseIndexValueTerm) + 4 + GetSize(c.CaseTerm)).Aggregate(0U, (acc, x) => x + acc) + GetSize(e.DefaultTerm),
                EX_Self => 0,
                EX_TextConst e =>
                    1 + e.Value.TextLiteralType switch
                    {
                        EBlueprintTextLiteralType.Empty => 0,
                        EBlueprintTextLiteralType.LocalizedText => GetSize(e.Value.LocalizedSource) + GetSize(e.Value.LocalizedKey) + GetSize(e.Value.LocalizedNamespace),
                        EBlueprintTextLiteralType.InvariantText => GetSize(e.Value.InvariantLiteralString),
                        EBlueprintTextLiteralType.LiteralString => GetSize(e.Value.LiteralString),
                        EBlueprintTextLiteralType.StringTableEntry => 8 + GetSize(e.Value.StringTableId) + GetSize(e.Value.StringTableKey),
                        _ => throw new NotImplementedException(),
                    },
                EX_ObjectConst e => 8,
                EX_VectorConst e => 12,
                EX_RotationConst e => 12,
                EX_TransformConst e => 40,
                EX_Context e => + GetSize(e.ObjectExpression) + 4 + 8 + GetSize(e.ContextExpression),
                EX_CallMulticastDelegate e => 8 + GetSize(e.Delegate) + e.Parameters.Select(p => GetSize(p)).Aggregate(0U, (acc, x) => x + acc) + 1,
                EX_LocalFinalFunction e => 8 + e.Parameters.Select(p => GetSize(p)).Aggregate(0U, (acc, x) => x + acc) + 1,
                EX_FinalFunction e => 8 + e.Parameters.Select(p => GetSize(p)).Aggregate(0U, (acc, x) => x + acc) + 1,
                EX_LocalVirtualFunction e => 12 + e.Parameters.Select(p => GetSize(p)).Aggregate(0U, (acc, x) => x + acc) + 1,
                EX_VirtualFunction e => 12 + e.Parameters.Select(p => GetSize(p)).Aggregate(0U, (acc, x) => x + acc) + 1,
                EX_InstanceVariable e => 8,
                EX_AddMulticastDelegate e => GetSize(e.Delegate) + GetSize(e.DelegateToAdd),
                EX_RemoveMulticastDelegate e => GetSize(e.Delegate) + GetSize(e.DelegateToAdd),
                EX_ClearMulticastDelegate e => GetSize(e.DelegateToClear),
                EX_BindDelegate e => 12 + GetSize(e.Delegate) + GetSize(e.ObjectTerm),
                EX_StructConst e => 8 + 4 + e.Value.Select(p => GetSize(p)).Aggregate(0U, (acc, x) => x + acc) + 1,
                EX_SetArray e => GetSize(e.AssigningProperty) + e.Elements.Select(p => GetSize(p)).Aggregate(0U, (acc, x) => x + acc) + 1,
                EX_SetMap e => GetSize(e.MapProperty) + 4 + e.Elements.Select(p => GetSize(p)).Aggregate(0U, (acc, x) => x + acc) + 1,
                EX_SetSet e => GetSize(e.SetProperty) + 4 + e.Elements.Select(p => GetSize(p)).Aggregate(0U, (acc, x) => x + acc) + 1,
                EX_SoftObjectConst e => GetSize(e.Value),
                EX_ByteConst e => 1,
                EX_IntConst e => 4,
                EX_FloatConst e => 4,
                EX_Int64Const e => 8,
                EX_UInt64Const e => 8,
                EX_NameConst e => 12,
                EX_StringConst e => (uint) e.Value.Length + 1,
                EX_UnicodeStringConst e => 2 * ((uint) e.Value.Length + 1),
                EX_SkipOffsetConst e => 4,
                EX_ArrayConst e => 12 + e.Elements.Select(p => GetSize(p)).Aggregate(0U, (acc, x) => x + acc) + 1,
                EX_Return e => GetSize(e.ReturnExpression),
                EX_LocalOutVariable e => 8,
                EX_InterfaceContext e => GetSize(e.InterfaceValue),
                EX_InterfaceToObjCast e => 8 + GetSize(e.Target),
                EX_ArrayGetByRef e => GetSize(e.ArrayVariable) + GetSize(e.ArrayIndex),
                EX_True e => 0,
                EX_False e => 0,
                EX_Nothing e => 0,
                EX_NoObject e => 0,
                EX_EndOfScript e => 0,
                EX_Tracepoint e => 0,
                EX_WireTracepoint e => 0,
                _ => throw new NotImplementedException(exp.ToString()),
            };
        }
    }
}
