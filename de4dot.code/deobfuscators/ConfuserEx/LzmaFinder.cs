using System;
using System.Collections.Generic;
using System.Linq;
using de4dot.blocks;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace de4dot.code.deobfuscators.ConfuserEx
{
    public class LzmaFinder
    {
        private readonly ISimpleDeobfuscator _deobfuscator;

        private readonly ModuleDef _module;

        public LzmaFinder(ModuleDef module, ISimpleDeobfuscator deobfuscator)
        {
            this._module = module;
            this._deobfuscator = deobfuscator;
        }

        public MethodDef Method { get; private set; }

        public List<TypeDef> Types { get; } = new List<TypeDef>();

        public bool FoundLzma => Method != null && Types.Count != 0;

        public void Find()
        {
            var moduleType = DotNetUtils.GetModuleType(_module);
            if (moduleType == null)
                return;
            foreach (var method in moduleType.Methods)
            {
                if (!method.HasBody || !method.IsStatic)
                    continue;
                if (!DotNetUtils.IsMethod(method, "System.Byte[]", "(System.Byte[])"))
                    continue;
                _deobfuscator.Deobfuscate(method, SimpleDeobfuscatorFlags.Force);
                if (!IsLzmaMethod(method))
                    continue;
                Method = method;
                var type = ((MethodDef) method.Body.Instructions[3].Operand).DeclaringType;
                ExtractNestedTypes(type);
            }
        }

        private bool IsLzmaMethod(MethodDef method)
        {
            var instructions = method.Body.Instructions;

            if (instructions.Count < 60)
                return false;

			var calledMethods = new Predicate<Instruction>[] {
				// newobj instance void [mscorlib]System.IO.MemoryStream::.ctor(uint8[], bool)
				i => i.Operand is IMethod im
					&& DotNetUtils.IsMethod(im, "System.Void", "(System.Byte[],System.Boolean)"),
				// newobj instance void [mscorlib]System.IO.MemoryStream::.ctor(uint8[])
				i => i.Operand is IMethod im
					&& DotNetUtils.IsMethod(im, "System.Void", "(System.Byte[])"),
				// callvirt instance int64 [mscorlib]System.IO.Stream::get_Length()
				i => i.Operand is IMethod im
					&& DotNetUtils.IsMethod(im, "System.Int64", "()"),
				// callvirt instance int32 [mscorlib]System.IO.Stream::Read(uint8[], int32, int32)
				i => i.Operand is IMethod im
					&& DotNetUtils.IsMethod(im, "System.Int32", "(System.Byte[],System.Int32,System.Int32)"),
				// call void [mscorlib]System.Array::Reverse(class [mscorlib]System.Array, int32, int32)
				// i => i.Operand is IMethod im
				// 	&& DotNetUtils.IsMethod(im, "System.Void", "(System.Array,System.Int32,System.Int32)"),
				// decoder.Code(s, z, compressedSize, outSize);
				i => i.Operand is IMethod im
					&& DotNetUtils.IsMethod(im, "System.Void", "(System.IO.Stream,System.IO.Stream,System.Int64,System.Int64)"),
			};

			foreach (var cm in calledMethods) {
				var flag = false;
				foreach (var i in instructions) {
					if (cm(i)) { flag = true; break; }
				}
				if (!flag)
					return false;
			}
			return true;
        }

        private void ExtractNestedTypes(TypeDef type)
        {
            foreach (var method in type.Methods)
                if (method.HasBody)
                {
                    var instr = method.Body.Instructions;
                    foreach (var inst in instr)
                        if (inst.Operand is MethodDef)
                        {
                            var ntype = (inst.Operand as MethodDef).DeclaringType;
                            if (!ntype.IsNested)
                                continue;
                            if (Types.Contains(ntype))
                                continue;
                            Types.Add(ntype);
                            ExtractNestedTypes(ntype);
                        }
                }
        }
    }
}
