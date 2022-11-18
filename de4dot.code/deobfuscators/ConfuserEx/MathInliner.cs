using System;
using System.Collections.Generic;
using System.Linq;
using de4dot.blocks;
using de4dot.blocks.cflow;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using static de4dot.code.deobfuscators.ConstantsReader;

namespace de4dot.code.deobfuscators.ConfuserEx {
	public class MathInliner : IBlocksDeobfuscator {
		public bool ExecuteIfNotModified { get; set; }
		public Dictionary<MethodDef, (OpCode OpCode, IMemberRef Target)> Map = new Dictionary<MethodDef, (OpCode, IMemberRef)> ();

		public MathInliner(ModuleDef module) {
			foreach (var method in module.GetTypes().SelectMany(t => t.Methods)) {
				if (!method.HasBody) {
					continue;
				}
				var ins = method.Body.Instructions;
				var match = true;
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
					}
					else {
						match = false;
					}
				}
				else {
					match = false;
				}

				if (match) {
					var target = ins[ins.Count - 2].Operand as IMemberRef;
					Map.Add(method, (ins[ins.Count - 2].OpCode, target));
				}
			}
		}

		public bool Deobfuscate(List<Block> allBlocks) {
			var modified = false;
			foreach (var block in allBlocks) {
				if (block.Instructions.Count == 0) {
					continue;
				}
				foreach (var ins in block.Instructions) {
					if (ins.OpCode == OpCodes.Call && ins.Operand is MethodDef md && Map.TryGetValue(md, out var result)) {
						modified = true;
						ins.Instruction.OpCode = result.OpCode;
						ins.Operand = result.Target;
					}
				}
				for (int i = 0; i < block.Instructions.Count - 1; i++) {
					var ins = block.Instructions[i];
					var nins = block.Instructions[i + 1];
					if (nins.Operand is MemberRef mr) {
						if (mr.DeclaringType.FullName == "System.Convert" && mr.Name == "ToInt32" && (ins.OpCode == OpCodes.Ldc_R8 || ins.OpCode == OpCodes.Ldc_R4)) {
							modified = true;
							ins.Instruction.OpCode = OpCodes.Ldc_I4;
							ins.Operand = Convert.ToInt32(ins.Operand);
							block.Remove(i + 1, 1);
						}
						if (mr.DeclaringType.FullName == "System.Int32" && mr.Name == "Parse" && ins.OpCode == OpCodes.Ldstr) {
							modified = true;
							ins.Instruction.OpCode = OpCodes.Ldc_I4;
							ins.Operand = int.Parse((string)ins.Operand);
							block.Remove(i + 1, 1);
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
								break;
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
		}
	}
}
