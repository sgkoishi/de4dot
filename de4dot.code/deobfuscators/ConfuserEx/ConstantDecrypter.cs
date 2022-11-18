using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.Arm;
using System.Text;
using de4dot.blocks;
using de4dot.blocks.cflow;
using de4dot.code.deobfuscators.ConfuserEx.x86;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;
using FieldAttributes = dnlib.DotNet.FieldAttributes;
using MethodAttributes = dnlib.DotNet.MethodAttributes;
using OpCodes = dnlib.DotNet.Emit.OpCodes;
using TypeAttributes = dnlib.DotNet.TypeAttributes;

namespace de4dot.code.deobfuscators.ConfuserEx
{
    public class ConstantDecrypterBase
    {
        private readonly InstructionEmulator _instructionEmulator = new InstructionEmulator();
        private X86Method _nativeMethod;

        public MethodDef Method { get; set; }
        public byte[] Decrypted { get; set; }
        public uint Magic1 { get; set; }
        public uint Magic2 { get; set; }
        public bool CanRemove { get; set; } = true;

        // native mode
        public MethodDef NativeMethod { get; internal set; }

        // normal mode
        public uint Num1 { get; internal set; }
        public uint Num2 { get; internal set; }

        private int? CalculateKey()
        {
            var popValue = _instructionEmulator.Peek();

            if (popValue == null || !popValue.IsInt32() || !(popValue as Int32Value).AllBitsValid())
                return null;

            _instructionEmulator.Pop();
            var result = _nativeMethod.Execute(((Int32Value) popValue).Value);
            return result;
        }

        private uint CalculateMagic(uint index)
        {
			if (NativeMethod != null) {
				_instructionEmulator.Push(new Int32Value((int)index));
				_nativeMethod = new X86Method(NativeMethod, Method.Module as ModuleDefMD); //TODO: Possible null
				var key = CalculateKey();

				var uint_0 = (uint)key.Value;

				uint_0 &= 0x3fffffff;
				uint_0 <<= 2;
				return uint_0;
			}
			else if (index is uint uint_0) {
				uint_0 = uint_0 * Num1 ^ Num2;

				uint_0 &= 0x3fffffff;
				uint_0 <<= 2;
				return uint_0;
			}
			throw new NotImplementedException();
		}

        public string DecryptString(uint index)
        {
			index = CalculateMagic(index);
            var count = BitConverter.ToInt32(Decrypted, (int) index);
            return string.Intern(Encoding.UTF8.GetString(Decrypted, (int) index + 4, count));
        }

        public T DecryptConstant<T>(uint index)
        {
			index = CalculateMagic(index);
            var array = new T[1];
            Buffer.BlockCopy(Decrypted, (int) index, array, 0, Marshal.SizeOf(typeof(T)));
            return array[0];
        }

        public byte[] DecryptArray(uint index)
        {
            index = CalculateMagic(index);
            var count = BitConverter.ToInt32(Decrypted, (int) index);
            //int lengt = BitConverter.ToInt32(Decrypted, (int)index+4);  we actualy dont need that
            var buffer = new byte[count - 4];
            Buffer.BlockCopy(Decrypted, (int) index + 8, buffer, 0, count - 4);
            return buffer;
        }
    }

    public class ConstantsDecrypter
    {
        private readonly ISimpleDeobfuscator _deobfuscator;
        private readonly MethodDef _lzmaMethod;

        private readonly ModuleDef _module;

        private readonly string[] _strDecryptCalledMethods =
        {
            "System.Text.Encoding System.Text.Encoding::get_UTF8()",
            "System.String System.Text.Encoding::GetString(System.Byte[],System.Int32,System.Int32)",
            "System.Array System.Array::CreateInstance(System.Type,System.Int32)",
            "System.String System.String::Intern(System.String)",
            "System.Void System.Buffer::BlockCopy(System.Array,System.Int32,System.Array,System.Int32,System.Int32)",
            "System.Type System.Type::GetTypeFromHandle(System.RuntimeTypeHandle)",
            "System.Type System.Type::GetElementType()"
        };

        private byte[] _decryptedBytes;
        private FieldDef _decryptedField, _arrayField;
        internal TypeDef ArrayType;

        public ConstantsDecrypter(ModuleDef module, MethodDef lzmaMethod, ISimpleDeobfuscator deobfsucator)
        {
            _module = module;
            _lzmaMethod = lzmaMethod;
            _deobfuscator = deobfsucator;
        }

        public bool CanRemoveLzma { get; private set; }

        public TypeDef Type => ArrayType;

        public MethodDef Method { get; private set; }

        public List<FieldDef> Fields => new List<FieldDef> {_decryptedField, _arrayField};

        public List<ConstantDecrypterBase> Decrypters { get; } = new List<ConstantDecrypterBase>();

        public bool Detected => Method != null && _decryptedBytes != null && Decrypters.Count != 0 &&
                                _decryptedField != null && _arrayField != null;

		public void Find() {
			var moduleCctor = DotNetUtils.GetModuleTypeCctor(_module);
			if (moduleCctor == null)
				return;
			foreach (var inst in moduleCctor.Body.Instructions) {
				if (inst.OpCode != OpCodes.Call)
					continue;
				if (!(inst.Operand is MethodDef))
					continue;
				var method = (MethodDef)inst.Operand;
				if (!method.HasBody || !method.IsStatic)
					continue;
				if (!DotNetUtils.IsMethod(method, "System.Void", "()"))
					continue;

				if (Find(method)) {
					Decrypters.AddRange(FindStringDecrypters(moduleCctor.DeclaringType));
				}
			}

			if (Find(moduleCctor)) {
				Decrypters.AddRange(FindStringDecrypters(moduleCctor.DeclaringType));
			}
		}

		public bool Find(MethodDef method) {
			_deobfuscator.Deobfuscate(method, SimpleDeobfuscatorFlags.Force);

			if (!IsStringDecrypterInit(method, out FieldDef aField, out FieldDef dField))
				return false;
			try {
				_decryptedBytes = DecryptArray(method, aField.InitialValue);
			}
			catch (Exception e) {
				Console.WriteLine(e.Message);
				return false;
			}

			_arrayField = aField;
			_decryptedField = dField;
			ArrayType = DotNetUtils.GetType(_module, _arrayField.FieldSig.Type);
			Method = method;
			CanRemoveLzma = true;
			return true;
		}

		private bool IsStringDecrypterInit(MethodDef method, out FieldDef aField, out FieldDef dField)
        {
            aField = null;
            dField = null;
            var instructions = method.Body.Instructions;
            if (instructions.Count < 15)
                return false;

			var calledMethods = new Predicate<Instruction>[] {
				// call void [mscorlib]System.Runtime.CompilerServices.RuntimeHelpers::InitializeArray(class [mscorlib]System.Array, valuetype [mscorlib]System.RuntimeFieldHandle)
				i => i.Operand is IMethod im
					&& DotNetUtils.IsMethod(im, "System.Void", "(System.Array,System.RuntimeFieldHandle)"),
				// call System.Byte[] Lzma.Decompress(System.Byte[])
				i => i.Operand is IMethod im
					&& im.DeclaringType.Name == "<Module>"
					&& DotNetUtils.IsMethod(im, "System.Byte[]", "(System.Byte[])"),
				// callvirt instance int64 [mscorlib]System.IO.Stream::get_Length()
				i => i.Operand is IMethod im
					&& DotNetUtils.IsMethod(im, "System.Int64", "()"),
				// callvirt instance int32 [mscorlib]System.IO.Stream::Read(uint8[], int32, int32)
				i => i.Operand is IMethod im
					&& DotNetUtils.IsMethod(im, "System.Int32", "(System.Byte[],System.Int32,System.Int32)"),
				// call void [mscorlib]System.Array::Reverse(class [mscorlib]System.Array, int32, int32)
				i => i.Operand is IMethod im
					&& DotNetUtils.IsMethod(im, "System.Void", "(System.Array,System.Int32,System.Int32)"),
				// decoder.Code(s, z, compressedSize, outSize);
				i => i.Operand is IMethod im
					&& DotNetUtils.IsMethod(im, "System.Void", "(System.IO.Stream,System.IO.Stream,System.Int64,System.Int64)"),
			};

			for (var i = 0; i < instructions.Count - 1; i++) {
				var ins = instructions[i];
				var nins = instructions[i + 1];
				if (ins.OpCode == OpCodes.Ldtoken) {
					if (nins.OpCode == OpCodes.Call
						&& ins.Operand is FieldDef fd && nins.Operand is IMethod im
						&& DotNetUtils.IsMethod(im, "System.Void", "(System.Array,System.RuntimeFieldHandle)")) {
						aField = fd;
					}
				}
				if (ins.OpCode == OpCodes.Call) {
					if (nins.OpCode == OpCodes.Stsfld
						&& ins.Operand is IMethod im && nins.Operand is FieldDef fd
						&& DotNetUtils.IsMethod(im, "System.Byte[]", "(System.Byte[])")
						&& fd.DeclaringType.ToString() == "<Module>") {
						dField = fd;
					}
				}
			}

			return aField != null && dField != null;
        }

        private byte[] DecryptArray(MethodDef method, byte[] encryptedArray)
        {
            ModuleDefUser tempModule = new ModuleDefUser("TempModule");

            AssemblyDef tempAssembly = new AssemblyDefUser("TempAssembly");
            tempAssembly.Modules.Add(tempModule);

            var tempType = new TypeDefUser("", "TempType", tempModule.CorLibTypes.Object.TypeDefOrRef);
            tempType.Attributes = TypeAttributes.Public | TypeAttributes.Class;
            MethodDef tempMethod = Utils.Clone(method);

            tempMethod.ReturnType = new SZArraySig(tempModule.CorLibTypes.Byte);
            tempMethod.MethodSig.Params.Add(new SZArraySig(tempModule.CorLibTypes.Byte));
            tempMethod.Attributes = MethodAttributes.Public | MethodAttributes.Static;

			// ldc.i4.s length
			// newarr System.UInt32
			// dup
			// ldtoken field valuetype '<Module>'/Struct '<Module>'::struct_
			// call void System.Runtime.CompilerServices.RuntimeHelpers::InitializeArray(class [mscorlib]System.Array, valuetype [mscorlib]System.RuntimeFieldHandle)

			// replace the default array with ldarg
			var index_start = -1;
			for (var i = 0; i < tempMethod.Body.Instructions.Count - 5; i++) {
				if (tempMethod.Body.Instructions[i].IsLdcI4()
					&& tempMethod.Body.Instructions[i + 1].OpCode == OpCodes.Newarr
					&& tempMethod.Body.Instructions[i + 2].OpCode == OpCodes.Dup
					&& tempMethod.Body.Instructions[i + 3].OpCode == OpCodes.Ldtoken
					&& tempMethod.Body.Instructions[i + 4].OpCode == OpCodes.Call) {
					index_start = i;
					break;
				}
			}

			// call System.Byte[] Decompress(System.Byte[])
			// stsfld <Module>.bytearray
			var index_end = -1;
			for (var i = 0; i < tempMethod.Body.Instructions.Count - 1; i++) {
				if (tempMethod.Body.Instructions[i].OpCode == OpCodes.Call
					&& tempMethod.Body.Instructions[i + 1].OpCode == OpCodes.Stsfld) {
					index_end = i;
					break;
				}
			}

			if (index_start > 0 && index_end > 0) {
				tempMethod.Body.Instructions.RemoveAt(index_end);
				tempMethod.Body.Instructions.RemoveAt(index_end);
				tempMethod.Body.Instructions.Insert(index_end, OpCodes.Ret.ToInstruction());
				while (tempMethod.Body.Instructions.Count > index_end + 1) {
					tempMethod.Body.Instructions.RemoveAt(index_end + 1);
				}
				tempMethod.Body.Instructions.RemoveAt(index_start);
				tempMethod.Body.Instructions.RemoveAt(index_start);
				tempMethod.Body.Instructions.RemoveAt(index_start);
				tempMethod.Body.Instructions.RemoveAt(index_start);
				tempMethod.Body.Instructions.RemoveAt(index_start);
				tempMethod.Body.Instructions.Insert(index_start, OpCodes.Ldarg_0.ToInstruction());
			}

			while (tempMethod.Body.Instructions.Count > 0) {
				if (!tempMethod.Body.Instructions[0].IsLdcI4()) {
					tempMethod.Body.Instructions.RemoveAt(0);
				}
				else {
					break;
				}
			}

            tempType.Methods.Add(tempMethod);
            tempModule.Types.Add(tempType);

            using (MemoryStream memoryStream = new MemoryStream())
            {
                ModuleWriterOptions moduleWriterOptions = new ModuleWriterOptions(tempModule);
                moduleWriterOptions.MetadataOptions = new MetadataOptions();

                tempModule.Write(memoryStream, moduleWriterOptions);

                Assembly patchedAssembly = Assembly.Load(memoryStream.ToArray());
                var type = patchedAssembly.ManifestModule.GetType("TempType");
                var methods = type.GetMethods();
                MethodInfo patchedMethod = methods.First(m => m.IsPublic && m.IsStatic);
                byte[] decryptedBytes = (byte[]) patchedMethod.Invoke(null, new object[]{encryptedArray});
                return Lzma.Decompress(decryptedBytes);
            }
        }

        private IEnumerable<ConstantDecrypterBase> FindStringDecrypters(TypeDef type)
        {
            foreach (var method in type.Methods)
            {
                if (!method.HasBody)
                    continue;
                if (!method.Signature.ContainsGenericParameter)
                    continue;
                var sig = method.MethodSig;
                if (sig?.Params.Count != 1)
                    continue;
                if (sig.Params[0].GetElementType() != ElementType.U4 && sig.Params[0].GetElementType() != ElementType.I4)
                    continue;
                if (!(sig.RetType.RemovePinnedAndModifiers() is GenericMVar))
                    continue;
                if (sig.GenParamCount != 1)
                    continue;

                _deobfuscator.Deobfuscate(method, SimpleDeobfuscatorFlags.Force);

                if (IsNativeStringDecrypter(method, out MethodDef nativeMethod))
                {
                    yield return new ConstantDecrypterBase
                    {
                        Decrypted = _decryptedBytes,
                        Method = method,
                        NativeMethod = nativeMethod
                    };
                }
                if (IsNormalStringDecrypter(method, out int num1, out int num2))
                {
                    yield return new ConstantDecrypterBase
                    {
                        Decrypted = _decryptedBytes,
                        Method = method,
                        Num1 = (uint)num1,
                        Num2 = (uint)num2
                    };
                }
            }
        }

        private bool IsNormalStringDecrypter(MethodDef method, out int num1, out int num2)
        {
            num1 = 0;
            num2 = 0;
            var instr = method.Body.Instructions;
            if (instr.Count < 25)
                return false;

			if (instr[0].OpCode == OpCodes.Call && instr[1].OpCode == OpCodes.Call && instr[2].OpCode == OpCodes.Callvirt) {
				// Assembly.GetExecutingAssembly().Equals(Assembly.GetCallingAssembly())
				if (instr[0].Operand is IMethod im0 && DotNetUtils.IsMethod(im0, "System.Reflection.Assembly", "()")
					&& instr[1].Operand is IMethod im1 && DotNetUtils.IsMethod(im1, "System.Reflection.Assembly", "()")
					&& instr[2].Operand is IMethod im2 && DotNetUtils.IsMethod(im2, "System.Boolean", "(System.Object)")) {
					instr[0].Operand = im1;
				}
			}

			for (var i = 0; i < instr.Count - 3; i++) {
				if (instr[i].IsLdcI4() && instr[i + 1].OpCode == OpCodes.Mul && instr[i + 2].IsLdcI4() && instr[i + 3].OpCode == OpCodes.Xor) {
					if (instr[i].Operand is int i1)
						num1 = i1;
					else
						num1 = (int)(uint)instr[i].Operand;
					if (instr[i + 2].Operand is int i2)
						num2 = i2;
					else
						num2 = (int)(uint)instr[i + 2].Operand;
					return true;
				}
			}

			return false;
        }

        private bool IsNativeStringDecrypter(MethodDef method, out MethodDef nativeMethod)
        {
            nativeMethod = null;
            var instr = method.Body.Instructions;
            if (instr.Count < 25)
                return false;

            var i = 0;

            if (!instr[i++].IsLdarg())
                return false;

            if (instr[i].OpCode != OpCodes.Call)
                return false;

            nativeMethod = instr[i++].Operand as MethodDef;

            if (nativeMethod == null || !nativeMethod.IsStatic || !nativeMethod.IsNative)
                return false;
            if (!DotNetUtils.IsMethod(nativeMethod, "System.Int32", "(System.Int32)"))
                return false;

            if (!instr[i++].IsStarg()) //uint_0 = (uint_0 * 2857448701u ^ 1196001109u);
                return false;

            if (!instr[i++].IsLdarg())
                return false;
            if (!instr[i].IsLdcI4() || instr[i++].GetLdcI4Value() != 0x1E)
                return false;
            if (instr[i++].OpCode != OpCodes.Shr_Un)
                return false;
            if (!instr[i++].IsStloc()) //uint num = uint_0 >> 30;
                return false;
            i++;
            //TODO: Implement
            //if (!instr[10].IsLdloca())
            //    return;
            if (instr[i++].OpCode != OpCodes.Initobj)
                return false;
            if (!instr[i++].IsLdarg())
                return false;
            if (!instr[i].IsLdcI4() || instr[i++].GetLdcI4Value() != 0x3FFFFFFF)
                return false;
            if (instr[i++].OpCode != OpCodes.And)
                return false;
            if (!instr[i++].IsStarg()) //uint_0 &= 1073741823u;
                return false;

            if (!instr[i++].IsLdarg())
                return false;
            if (!instr[i].IsLdcI4() || instr[i++].GetLdcI4Value() != 2)
                return false;
            if (instr[i++].OpCode != OpCodes.Shl)
                return false;
            if (!instr[i++].IsStarg()) //uint_0 <<= 2;
                return false;

            foreach (var mtd in _strDecryptCalledMethods)
                if (!DotNetUtils.CallsMethod(method, mtd))
                    return false;
            //TODO: Implement
            //if (!DotNetUtils.LoadsField(method, decryptedField))
            //    return;
            return true;
        }

        private static bool VerifyGenericArg(MethodSpec gim, ElementType etype)
        {
            var gims = gim?.GenericInstMethodSig;
            if (gims == null || gims.GenericArguments.Count != 1)
                return false;
            return gims.GenericArguments[0].GetElementType() == etype;
        }

        public string DecryptString(ConstantDecrypterBase info, MethodSpec gim, uint magic1)
        {
            if (!VerifyGenericArg(gim, ElementType.String))
                return null;
            return info.DecryptString(magic1);
        }

        public object DecryptSByte(ConstantDecrypterBase info, MethodSpec gim, uint magic1)
        {
            if (!VerifyGenericArg(gim, ElementType.I1))
                return null;
            return info.DecryptConstant<sbyte>(magic1);
        }

        public object DecryptByte(ConstantDecrypterBase info, MethodSpec gim, uint magic1)
        {
            if (!VerifyGenericArg(gim, ElementType.U1))
                return null;
            return info.DecryptConstant<byte>(magic1);
        }

        public object DecryptInt16(ConstantDecrypterBase info, MethodSpec gim, uint magic1)
        {
            if (!VerifyGenericArg(gim, ElementType.I2))
                return null;
            return info.DecryptConstant<short>(magic1);
        }

        public object DecryptUInt16(ConstantDecrypterBase info, MethodSpec gim, uint magic1)
        {
            if (!VerifyGenericArg(gim, ElementType.U2))
                return null;
            return info.DecryptConstant<ushort>(magic1);
        }

        public object DecryptInt32(ConstantDecrypterBase info, MethodSpec gim, uint magic1)
        {
            if (!VerifyGenericArg(gim, ElementType.I4))
                return null;
            return info.DecryptConstant<int>(magic1);
        }

        public object DecryptUInt32(ConstantDecrypterBase info, MethodSpec gim, uint magic1)
        {
            if (!VerifyGenericArg(gim, ElementType.U4))
                return null;
            return info.DecryptConstant<uint>(magic1);
        }

        public object DecryptInt64(ConstantDecrypterBase info, MethodSpec gim, uint magic1)
        {
            if (!VerifyGenericArg(gim, ElementType.I8))
                return null;
            return info.DecryptConstant<long>(magic1);
        }

        public object DecryptUInt64(ConstantDecrypterBase info, MethodSpec gim, uint magic1)
        {
            if (!VerifyGenericArg(gim, ElementType.U8))
                return null;
            return info.DecryptConstant<ulong>(magic1);
        }

        public object DecryptSingle(ConstantDecrypterBase info, MethodSpec gim, uint magic1)
        {
            if (!VerifyGenericArg(gim, ElementType.R4))
                return null;
            return info.DecryptConstant<float>(magic1);
        }

        public object DecryptDouble(ConstantDecrypterBase info, MethodSpec gim, uint magic1)
        {
            if (!VerifyGenericArg(gim, ElementType.R8))
                return null;
            return info.DecryptConstant<double>(magic1);
        }

        public object DecryptArray(ConstantDecrypterBase info, MethodSpec gim, uint magic1)
        {
            if (!VerifyGenericArg(gim, ElementType.SZArray))
                return null;
            return info.DecryptArray(magic1);
        }
    }
}
