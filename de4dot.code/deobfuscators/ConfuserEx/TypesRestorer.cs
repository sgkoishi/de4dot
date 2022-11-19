/*
    Copyright (C) 2011-2015 de4dot@gmail.com

    This file is part of de4dot.

    de4dot is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    de4dot is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with de4dot.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using dnlib.DotNet;

namespace de4dot.code.deobfuscators.ConfuserEx {
	/// <summary>
	/// The <seealso cref="de4dot.code.deobfuscators.TypesRestorerBase"/> keep everything private so not using it
	/// </summary>
	public class TypesRestorer {
		[DebuggerDisplay("{callee}")]
		public class Call {
			public MethodDef method;
			public List<MethodDef> caller;
			public List<(GenericParam Key, List<TypeSig> Value)> callerSigs;
			public List<int> Cleaned() {
				var list = new List<int>();
				for (var i = 0; i < this.callerSigs.Count; i++) {
					if (this.callerSigs[i].Value.Count == 1) {
						list.Add(i);
					}
				}
				list.Reverse();
				return list;
			}
		}

		ModuleDef module;
		List<Call> methods;

		public TypesRestorer(ModuleDef module) {
			this.module = module;
		}

		public void Deobfuscate() {
			for (var i = 0; i < 20; i++) {
				FindAllGenericMethods();
				ScanGenericRelation();
				if (!RestoreTypes())
					break;
			}
			Cleanup();
		}

		void FindAllGenericMethods() {
			methods = module.GetTypes()
				.SelectMany(t => t.Methods)
				.Where(m => m.HasGenericParameters && m.DeclaringType != module.GlobalType)
				.Select(m => new Call {
					method = m,
					caller = new List<MethodDef>(),
					callerSigs = m.GenericParameters.Select(g => (g, new List<TypeSig>())).ToList()
				})
				.ToList();
		}

		void ScanGenericRelation() {
			foreach (var method in module.GetTypes().SelectMany(t => t.Methods)) {
				if (!method.HasBody)
					continue;
				foreach (var ins in method.Body.Instructions) {
					if (ins.Operand is MethodSpec ms) {
						var mdef = ms.ResolveMethodDef();

						foreach (var calls in methods) {
							if (calls.method != mdef)
								continue;
							if (!calls.caller.Contains(method))
								calls.caller.Add(method);
							for (int i = 0; i < ms.GenericInstMethodSig.GenericArguments.Count; i++) {
								var sig = ms.GenericInstMethodSig.GenericArguments[i];
								if (!calls.callerSigs[i].Value.Contains(sig))
									calls.callerSigs[i].Value.Add(sig);
							}
							break;
						}
					}
				}
			}
		}

		bool RestoreTypes() {
			var changed = false;
			foreach (var mc in methods) {
				var method = mc.method;
				{
					if (Specify(method.ReturnType, mc, out var result)) {
						changed = true;
						method.ReturnType = result;
					}
				}
				foreach (var local in method.Body.Variables) {
					if (Specify(local.Type, mc, out var result)) {
						changed = true;
						local.Type = result;
					}
				}
				foreach (var param in method.Parameters) {
					if (Specify(param.Type, mc, out var result)) {
						changed = true;
						param.Type = result;
					}
				}
				changed |= RestoreCallees(mc);
			}
			return changed;
		}

		bool RestoreCallees(Call relation) {
			var method = relation.method;
			if (!method.HasBody)
				return false;

			var changed = false;
			foreach (var ins in method.Body.Instructions) {
				if (ins.Operand is ITypeDefOrRef tdr) {
					if (Specify(tdr.ToTypeSig(), relation, out var result)) {
						changed = true;
						ins.Operand = result.ToTypeDefOrRef();
					}
				}
			}
			return changed;
		}

		void Cleanup() {
			foreach (var mc in methods) {
				RestoreSignature(mc);
				RestoreCallers(mc);
			}
		}

		void RestoreSignature(Call relation) {
			foreach (var item in relation.Cleaned()) {
				relation.method.GenericParameters.RemoveAt(item);
			}
		}

		void RestoreCallers(Call relation) {
			foreach (var method in relation.caller) {
				if (!method.HasBody)
					continue;
				foreach (var ins in method.Body.Instructions) {
					if (ins.Operand is MethodSpec ms) {
						var mdef = ms.ResolveMethodDef();
						if (mdef != relation.method)
							continue;

						foreach (var item in relation.Cleaned()) {
							ms.GenericInstMethodSig.GenericArguments.RemoveAt(item);
						}

						if (ms.GenericInstMethodSig.GenericArguments.Count == 0) {
							ins.Operand = mdef;
						}
					}
				}
			}
		}

		bool Specify(TypeSig type, Call relation, out TypeSig result) {
			result = type;
			foreach (var kvp in relation.callerSigs) {
				if (kvp.Value.Count == 1) {
					if (Specify(type, kvp.Key, kvp.Value[0], out result)) {
						return true;
					}
				}
			}
			return false;
		}

		bool Specify(TypeSig type, GenericParam gp, TypeSig hint, out TypeSig result) {
			result = type;
			if (type is GenericMVar gmv && gmv.GenericParam == gp) {
				result = hint;
				return true;
			}
			if (type.Next != null) {
				if (!Specify(type.Next, gp, hint, out result))
					return false;
				if (type is ArraySig asig) {
					if (asig.Rank == 1)
						result = new SZArraySig(result);
					else
						result = new ArraySig(result);
					return true;
				}
				if (type is SZArraySig) {
					result = new SZArraySig(result);
					return true;
				}
			}
			return false;
		}
	}
}
