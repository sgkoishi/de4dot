using System;
using System.Collections.Generic;
using System.Linq;
using de4dot.blocks;
using de4dot.blocks.cflow;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using static de4dot.code.deobfuscators.ConstantsReader;

namespace de4dot.code.deobfuscators.ConfuserEx {
	public class MiscInliner : IBlocksDeobfuscator {
		public bool ExecuteIfNotModified { get; set; }
		public Dictionary<MethodDef, (OpCode OpCode, IMemberRef Target)> Map = new Dictionary<MethodDef, (OpCode, IMemberRef)>();

		public bool ShouldInline(MethodDef method, out OpCode opcode, out IMemberRef target) {
			void Detect(MethodDef method) {
				if (!method.HasBody) {
					return;
				}
				var match = true;
				var ins = method.Body.Instructions;
				if (ins.Count >= 2) {
					if (ins.Last().OpCode == OpCodes.Ret) {
						for (var i = 0; i < ins.Count - 2; i++) {
							if (!ins[i].IsLdarg()) {
								match = false;
								break;
							}
							if (ins[i].GetParameterIndex() != i) {
								match = false;
								break;
							}
						}
						if (ins.Count - 2 != method.Parameters.Count) {
							match = false;
						}

						if (match) {
							var target = ins[ins.Count - 2].Operand as IMemberRef;
							Map.Add(method, (ins[ins.Count - 2].OpCode, target));
						}
					}
				}
				else {
					if (ins.Count == 1 && ins[0].OpCode == OpCodes.Ret && method.Parameters.Count == 0) {
						if (method.IsStatic) {
							Map.Add(method, (OpCodes.Nop, null));
						}
						else {
							// Does `this` count? idk
							// Map.Add(method, (OpCodes.Pop, null));
						}
					}
				}
			}

			if (!Map.ContainsKey(method)) {
				Detect(method);
			}

			if (Map.TryGetValue(method, out var result)) {
				opcode = result.OpCode;
				target = result.Target;
				return true;
			}
			else {
				opcode = null;
				target = null;
				return false;
			}
		}

		public bool Deobfuscate(List<Block> allBlocks) {
			var modified = false;
			foreach (var block in allBlocks) {
				if (block.Instructions.Count == 0) {
					continue;
				}
				foreach (var ins in block.Instructions) {
					if (ins.OpCode == OpCodes.Call && ins.Operand is MethodDef md && ShouldInline(md, out var opc, out var tgt)) {
						modified = true;
						ins.Instruction.OpCode = opc;
						ins.Operand = tgt;
					}
				}
				for (int i = 0; i < block.Instructions.Count - 1; i++) {
					var ins = block.Instructions[i];
					var nins = block.Instructions[i + 1];
					if (ins.OpCode == OpCodes.Ldtoken) {
						if (ins.Operand is TypeSpec ts
							&& nins.OpCode == OpCodes.Call && nins.Operand is IMethod im1
							&& DotNetUtils.IsMethod(im1, "System.Type", "(System.RuntimeTypeHandle)")
							&& block.Instructions[i + 2].OpCode == OpCodes.Call && block.Instructions[i + 2].Operand is IMethod im2
							&& DotNetUtils.IsMethod(im2, "System.Object", "(System.Type)")) {
							modified = true;
							ins.Instruction.OpCode = OpCodes.Newobj;
							// FIXME
							if (ts.ContainsGenericParameter) {
								ins.Operand = new MethodSpecUser(new MemberRefUser(current.Module, ".ctor", MethodSig.CreateInstanceGeneric(1, current.Module.CorLibTypes.Void), ts));
							} else {
								ins.Operand = new MemberRefUser(current.Module, ".ctor", MethodSig.CreateInstance(current.Module.CorLibTypes.Void), ts);
							}
							block.Remove(i + 1, 2);
							continue;
						}
					}
					if (ins.OpCode == OpCodes.Ldsfld && nins.OpCode == OpCodes.Ldlen) {
						if (ins.Operand is IField f && f.DeclaringType.FullName == "System.Type" && f.Name == "EmptyTypes") {
							modified = true;
							ins.Instruction.OpCode = OpCodes.Ldc_I4_0;
							block.Remove(i + 1, 1);
							continue;
						}
					}
					if (nins.Operand is MemberRef mr) {
						if (mr.DeclaringType.FullName == "System.Convert" && mr.Name == "ToInt32" && (ins.OpCode == OpCodes.Ldc_R8 || ins.OpCode == OpCodes.Ldc_R4)) {
							modified = true;
							ins.Instruction.OpCode = OpCodes.Ldc_I4;
							ins.Operand = Convert.ToInt32(ins.Operand);
							block.Remove(i + 1, 1);
							continue;
						}
						if (mr.DeclaringType.FullName == "System.Int32" && mr.Name == "Parse" && ins.OpCode == OpCodes.Ldstr) {
							modified = true;
							ins.Instruction.OpCode = OpCodes.Ldc_I4;
							ins.Operand = int.Parse((string)ins.Operand);
							block.Remove(i + 1, 1);
							continue;
						}
						if (mr.DeclaringType.FullName == "System.Single" && mr.Name == "Parse" && ins.OpCode == OpCodes.Ldstr) {
							modified = true;
							ins.Instruction.OpCode = OpCodes.Ldc_R4;
							ins.Operand = float.Parse((string)ins.Operand);
							block.Remove(i + 1, 1);
							continue;
						}
						if (mr.DeclaringType.FullName == "System.Double" && mr.Name == "Parse" && ins.OpCode == OpCodes.Ldstr) {
							modified = true;
							ins.Instruction.OpCode = OpCodes.Ldc_R8;
							ins.Operand = double.Parse((string)ins.Operand);
							block.Remove(i + 1, 1);
							continue;
						}
						if (mr.DeclaringType.FullName == "System.Math" && ins.OpCode == OpCodes.Ldc_R8) {
							switch (mr.Name) {
							case "Abs":
							case "Floor":
							case "Ceiling":
							case "Log10":
							case "Round":
							case "Sin":
							case "Cos":
							case "Tan":
							case "Log":
							case "Sqrt":
							case "Truncate":
							case "Tanh":
								modified = true;
								var target = typeof(Math).GetMethod(mr.Name, new[] { typeof(double) })!;
								var result = target.Invoke(null, new[] { ins.Operand });
								ins.Operand = result;
								block.Remove(i + 1, 1);
								continue;
							default:
								break;
								throw new NotImplementedException(mr.Name);
							}
						}
					}
				}
			}
			return modified;
		}

		public void DeobfuscateBegin(Blocks blocks) {
			current = blocks.Method;
		}
		private MethodDef current;
	}
}
