/*
Copyright (C) 2018-2019 de4dot@gmail.com

Permission is hereby granted, free of charge, to any person obtaining
a copy of this software and associated documentation files (the
"Software"), to deal in the Software without restriction, including
without limitation the rights to use, copy, modify, merge, publish,
distribute, sublicense, and/or sell copies of the Software, and to
permit persons to whom the Software is furnished to do so, subject to
the following conditions:

The above copyright notice and this permission notice shall be
included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.
IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY
CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE
SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

#if !NO_DECODER
using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Iced.Intel.DecoderInternal;

namespace Iced.Intel {
	enum OpSize : byte {
		Size16,
		Size32,
		Size64,
	}

	// GENERATOR-BEGIN: StateFlags
	// ⚠️This was generated by GENERATOR!🦹‍♂️
	[Flags]
	enum StateFlags : uint {
		EncodingMask = 0x00000007,
		HasRex = 0x00000008,
		b = 0x00000010,
		z = 0x00000020,
		IsInvalid = 0x00000040,
		W = 0x00000080,
		NoImm = 0x00000100,
		Addr64 = 0x00000200,
		BranchImm8 = 0x00000400,
		Xbegin = 0x00000800,
		Lock = 0x00001000,
		AllowLock = 0x00002000,
	}
	// GENERATOR-END: StateFlags

	/// <summary>
	/// Decodes 16/32/64-bit x86 instructions
	/// </summary>
	public sealed class Decoder {
		ulong instructionPointer;
		readonly CodeReader reader;
		readonly uint[] prefixes;
		readonly RegInfo2[] memRegs16;
		readonly OpCodeHandler[] handlers_XX;
		readonly OpCodeHandler[] handlers_VEX_0FXX;
		readonly OpCodeHandler[] handlers_VEX_0F38XX;
		readonly OpCodeHandler[] handlers_VEX_0F3AXX;
		readonly OpCodeHandler[] handlers_EVEX_0FXX;
		readonly OpCodeHandler[] handlers_EVEX_0F38XX;
		readonly OpCodeHandler[] handlers_EVEX_0F3AXX;
		readonly OpCodeHandler[] handlers_XOP8;
		readonly OpCodeHandler[] handlers_XOP9;
		readonly OpCodeHandler[] handlers_XOPA;
		internal State state;
		internal uint displIndex;
		internal readonly DecoderOptions options;
		internal readonly uint invalidCheckMask;// All 1s if we should check for invalid instructions, else 0
		internal readonly uint is64Mode_and_W;// StateFlags.W if 64-bit, 0 if 16/32-bit
		internal readonly CodeSize defaultCodeSize;
		readonly OpSize defaultOperandSize;
		readonly OpSize defaultAddressSize;
		readonly OpSize defaultInvertedOperandSize;
		readonly OpSize defaultInvertedAddressSize;
		internal readonly bool is64Mode;

		internal struct State {
			public uint modrm, mod, reg, rm;
			public uint instructionLength;
			public uint extraRegisterBase;		// R << 3
			public uint extraIndexRegisterBase;	// X << 3
			public uint extraBaseRegisterBase;	// B << 3
			public uint extraIndexRegisterBaseVSIB;
			public StateFlags flags;
			public MandatoryPrefixByte mandatoryPrefix;
			public uint vvvv;// V`vvvv. Not stored in inverted form. If 16/32-bit, bits [4:3] are cleared
			public uint aaa;
			public uint extraRegisterBaseEVEX;
			public uint extraBaseRegisterBaseEVEX;
			public uint vectorLength;
			public OpSize operandSize;
			public OpSize addressSize;
			public readonly EncodingKind Encoding => (EncodingKind)(flags & StateFlags.EncodingMask);
		}

		/// <summary>
		/// Current <c>IP</c>/<c>EIP</c>/<c>RIP</c> value
		/// </summary>
		public ulong IP {
			get => instructionPointer;
			set => instructionPointer = value;
		}

		/// <summary>
		/// Gets the bitness (16, 32 or 64)
		/// </summary>
		public int Bitness { get; }

		// 26,2E,36,3E,64,65,66,67,F0,F2,F3
		static readonly uint[] prefixes1632 = new uint[8] {
			0x00000000, 0x40404040, 0x00000000, 0x000000F0,
			0x00000000, 0x00000000, 0x00000000, 0x000D0000,
		};
		// 26,2E,36,3E,64,65,66,67,F0,F2,F3 and 40-4F
		static readonly uint[] prefixes64 = new uint[8] {
			0x00000000, 0x40404040, 0x0000FFFF, 0x000000F0,
			0x00000000, 0x00000000, 0x00000000, 0x000D0000,
		};

		static Decoder() {
			// Initialize cctors that are used by decoder related methods. It doesn't speed up
			// decoding much, but getting instruction info is a little faster.
			_ = OpCodeHandler_Invalid.Instance;
			_ = InstructionMemorySizes.Sizes;
			_ = OpCodeHandler_D3NOW.CodeValues;
			_ = InstructionOpCounts.OpCount;
			_ = MnemonicUtils.toMnemonic;
#if !NO_INSTR_INFO
			_ = RegisterExtensions.RegisterInfos;
			_ = MemorySizeExtensions.MemorySizeInfos;
			_ = InstructionInfoInternal.InstrInfoTable.Data;
			_ = InstructionInfoInternal.RflagsInfoConstants.flagsCleared;
			_ = InstructionInfoInternal.CpuidFeatureInternalData.ToCpuidFeatures;
			_ = InstructionInfoInternal.SimpleList<UsedRegister>.Empty;
			_ = InstructionInfoInternal.SimpleList<UsedMemory>.Empty;
#endif
		}

		Decoder(CodeReader reader, DecoderOptions options, int bitness) {
			this.reader = reader ?? throw new ArgumentNullException(nameof(reader));
			this.options = options;
			invalidCheckMask = (options & DecoderOptions.NoInvalidCheck) == 0 ? uint.MaxValue : 0;
			memRegs16 = s_memRegs16;
			Bitness = bitness;
			if (bitness == 64) {
				is64Mode = true;
				defaultCodeSize = CodeSize.Code64;
				defaultOperandSize = OpSize.Size32;
				defaultInvertedOperandSize = OpSize.Size16;
				defaultAddressSize = OpSize.Size64;
				defaultInvertedAddressSize = OpSize.Size32;
				prefixes = prefixes64;
			}
			else if (bitness == 32) {
				is64Mode = false;
				defaultCodeSize = CodeSize.Code32;
				defaultOperandSize = OpSize.Size32;
				defaultInvertedOperandSize = OpSize.Size16;
				defaultAddressSize = OpSize.Size32;
				defaultInvertedAddressSize = OpSize.Size16;
				prefixes = prefixes1632;
			}
			else {
				Debug.Assert(bitness == 16);
				is64Mode = false;
				defaultCodeSize = CodeSize.Code16;
				defaultOperandSize = OpSize.Size16;
				defaultInvertedOperandSize = OpSize.Size32;
				defaultAddressSize = OpSize.Size16;
				defaultInvertedAddressSize = OpSize.Size32;
				prefixes = prefixes1632;
			}
			is64Mode_and_W = is64Mode ? (uint)StateFlags.W : 0;
			handlers_XX = OpCodeHandlersTables_Legacy.OneByteHandlers;
			handlers_VEX_0FXX = OpCodeHandlersTables_VEX.TwoByteHandlers_0FXX;
			handlers_VEX_0F38XX = OpCodeHandlersTables_VEX.ThreeByteHandlers_0F38XX;
			handlers_VEX_0F3AXX = OpCodeHandlersTables_VEX.ThreeByteHandlers_0F3AXX;
			handlers_EVEX_0FXX = OpCodeHandlersTables_EVEX.TwoByteHandlers_0FXX;
			handlers_EVEX_0F38XX = OpCodeHandlersTables_EVEX.ThreeByteHandlers_0F38XX;
			handlers_EVEX_0F3AXX = OpCodeHandlersTables_EVEX.ThreeByteHandlers_0F3AXX;
			handlers_XOP8 = OpCodeHandlersTables_XOP.XOP8;
			handlers_XOP9 = OpCodeHandlersTables_XOP.XOP9;
			handlers_XOPA = OpCodeHandlersTables_XOP.XOPA;
		}

		/// <summary>
		/// Creates a decoder
		/// </summary>
		/// <param name="bitness">16, 32 or 64</param>
		/// <param name="reader">Code reader</param>
		/// <param name="options">Decoder options</param>
		/// <returns></returns>
		public static Decoder Create(int bitness, CodeReader reader, DecoderOptions options = DecoderOptions.None) {
			switch (bitness) {
			case 16:
			case 32:
			case 64: return new Decoder(reader, options, bitness);
			default: throw new ArgumentOutOfRangeException(nameof(bitness));
			}
		}

		internal uint ReadByte() {
			uint instrLen = state.instructionLength;
			if (instrLen < IcedConstants.MaxInstructionLength) {
				uint b = (uint)reader.ReadByte();
				Debug.Assert(b <= byte.MaxValue || b > int.MaxValue);
				if (b <= byte.MaxValue) {
					state.instructionLength = instrLen + 1;
					return b;
				}
			}
			state.flags |= StateFlags.IsInvalid;
			return 0;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		internal uint ReadUInt16() => ReadByte() | (ReadByte() << 8);
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		internal uint ReadUInt32() => ReadByte() | (ReadByte() << 8) | (ReadByte() << 16) | (ReadByte() << 24);

		/// <summary>
		/// Decodes the next instruction, see also <see cref="Decode(out Instruction)"/> which is faster
		/// if you already have an <see cref="Instruction"/> local, array element or field.
		/// </summary>
		/// <returns></returns>
		public Instruction Decode() {
			Decode(out var instr);
			return instr;
		}

		/// <summary>
		/// Decodes the next instruction
		/// </summary>
		/// <param name="instruction">Decoded instruction</param>
		public void Decode(out Instruction instruction) {
			instruction = default;
			// JIT32: it's 9% slower decoding instructions if we clear the whole 'state'
			// 32-bit RyuJIT: not tested
			// 64-bit RyuJIT: diff is too small to care about
#if truex
			state = default;
#else
			state.instructionLength = 0;
			state.extraRegisterBase = 0;
			state.extraIndexRegisterBase = 0;
			state.extraBaseRegisterBase = 0;
			state.extraIndexRegisterBaseVSIB = 0;
			state.flags = 0;
			state.mandatoryPrefix = 0;
#endif
			state.operandSize = defaultOperandSize;
			state.addressSize = defaultAddressSize;
			var defaultDsSegment = (byte)Register.DS;
			uint rexPrefix = 0;
			uint b;
			for (;;) {
				b = ReadByte();
				// RyuJIT32: 2-5% faster, RyuJIT64: almost no improvement
				if (((prefixes[(int)(b / 32)] >> ((int)b & 31)) & 1) == 0)
					break;
				// Converting these prefixes to opcode handlers instead of a switch results in slightly worse perf
				// with JIT32, and about the same speed with 64-bit RyuJIT.
				switch (b) {
				case 0x26:
					if (!is64Mode || (defaultDsSegment != (byte)Register.FS && defaultDsSegment != (byte)Register.GS)) {
						instruction.SegmentPrefix = Register.ES;
						defaultDsSegment = (byte)Register.ES;
					}
					rexPrefix = 0;
					break;

				case 0x2E:
					if (!is64Mode || (defaultDsSegment != (byte)Register.FS && defaultDsSegment != (byte)Register.GS)) {
						instruction.SegmentPrefix = Register.CS;
						defaultDsSegment = (byte)Register.CS;
					}
					rexPrefix = 0;
					break;

				case 0x36:
					if (!is64Mode || (defaultDsSegment != (byte)Register.FS && defaultDsSegment != (byte)Register.GS)) {
						instruction.SegmentPrefix = Register.SS;
						defaultDsSegment = (byte)Register.SS;
					}
					rexPrefix = 0;
					break;

				case 0x3E:
					if (!is64Mode || (defaultDsSegment != (byte)Register.FS && defaultDsSegment != (byte)Register.GS)) {
						instruction.SegmentPrefix = Register.DS;
						defaultDsSegment = (byte)Register.DS;
					}
					rexPrefix = 0;
					break;

				case 0x64:
					instruction.SegmentPrefix = Register.FS;
					defaultDsSegment = (byte)Register.FS;
					rexPrefix = 0;
					break;

				case 0x65:
					instruction.SegmentPrefix = Register.GS;
					defaultDsSegment = (byte)Register.GS;
					rexPrefix = 0;
					break;

				case 0x66:
					state.operandSize = defaultInvertedOperandSize;
					if (state.mandatoryPrefix == MandatoryPrefixByte.None)
						state.mandatoryPrefix = MandatoryPrefixByte.P66;
					rexPrefix = 0;
					break;

				case 0x67:
					state.addressSize = defaultInvertedAddressSize;
					rexPrefix = 0;
					break;

				case 0xF0:
					instruction.InternalSetHasLockPrefix();
					state.flags |= StateFlags.Lock;
					rexPrefix = 0;
					break;

				case 0xF2:
					instruction.InternalSetHasRepnePrefix();
					state.mandatoryPrefix = MandatoryPrefixByte.PF2;
					rexPrefix = 0;
					break;

				case 0xF3:
					instruction.InternalSetHasRepePrefix();
					state.mandatoryPrefix = MandatoryPrefixByte.PF3;
					rexPrefix = 0;
					break;

				default:
					Debug.Assert(is64Mode);
					Debug.Assert(0x40 <= b && b <= 0x4F);
					rexPrefix = b;
					break;
				}
			}
			if (rexPrefix != 0) {
				state.flags |= StateFlags.HasRex;
				if ((rexPrefix & 8) != 0) {
					state.operandSize = OpSize.Size64;
					state.flags |= StateFlags.W;
				}
				state.extraRegisterBase = (rexPrefix & 4) << 1;
				state.extraIndexRegisterBase = (rexPrefix & 2) << 2;
				state.extraBaseRegisterBase = (rexPrefix & 1) << 3;
			}
			DecodeTable(handlers_XX[b], ref instruction);
			var flags = state.flags;
			if ((flags & (StateFlags.IsInvalid | StateFlags.Lock)) != 0) {
				if ((flags & StateFlags.IsInvalid) != 0 ||
					(((uint)(flags & (StateFlags.Lock | StateFlags.AllowLock)) & invalidCheckMask) == (uint)StateFlags.Lock)) {
					instruction = default;
					Static.Assert(Code.INVALID == 0 ? 0 : -1);
					//instruction.InternalCode = Code.INVALID;
				}
			}
			instruction.InternalCodeSize = defaultCodeSize;
			uint instrLen = state.instructionLength;
			Debug.Assert(0 <= instrLen && instrLen <= IcedConstants.MaxInstructionLength);// Could be 0 if there were no bytes available
			instruction.InternalLength = instrLen;
			var ip = instructionPointer;
			ip += instrLen;
			instructionPointer = ip;
			instruction.NextIP = ip;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		internal uint GetCurrentInstructionPointer32() => (uint)instructionPointer + state.instructionLength;
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		internal ulong GetCurrentInstructionPointer64() => instructionPointer + state.instructionLength;

		internal void ClearMandatoryPrefix(ref Instruction instruction) {
			Debug.Assert(state.Encoding == EncodingKind.Legacy);
			switch (state.mandatoryPrefix) {
			case MandatoryPrefixByte.P66:
				state.operandSize = defaultOperandSize;
				break;
			case MandatoryPrefixByte.PF3:
				instruction.InternalClearHasRepePrefix();
				break;
			case MandatoryPrefixByte.PF2:
				instruction.InternalClearHasRepnePrefix();
				break;
			}
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		internal void SetXacquireXrelease(ref Instruction instruction, HandlerFlags flags) {
			if ((flags & HandlerFlags.XacquireXreleaseNoLock) != 0 || instruction.HasLockPrefix)
				SetXacquireXreleaseCore(ref instruction, flags);
		}

		void SetXacquireXreleaseCore(ref Instruction instruction, HandlerFlags flags) {
			Debug.Assert(!((flags & HandlerFlags.XacquireXreleaseNoLock) == 0 && !instruction.HasLockPrefix));
			switch (state.mandatoryPrefix) {
			case MandatoryPrefixByte.PF2:
				if ((flags & HandlerFlags.Xacquire) != 0) {
					ClearMandatoryPrefixF2(ref instruction);
					instruction.InternalSetHasXacquirePrefix();
				}
				break;

			case MandatoryPrefixByte.PF3:
				if ((flags & HandlerFlags.Xrelease) != 0) {
					ClearMandatoryPrefixF3(ref instruction);
					instruction.InternalSetHasXreleasePrefix();
				}
				break;
			}
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		internal void ClearMandatoryPrefixF3(ref Instruction instruction) {
			Debug.Assert(state.Encoding == EncodingKind.Legacy);
			Debug.Assert(state.mandatoryPrefix == MandatoryPrefixByte.PF3);
			instruction.InternalClearHasRepePrefix();
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		internal void ClearMandatoryPrefixF2(ref Instruction instruction) {
			Debug.Assert(state.Encoding == EncodingKind.Legacy);
			Debug.Assert(state.mandatoryPrefix == MandatoryPrefixByte.PF2);
			instruction.InternalClearHasRepnePrefix();
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		internal void SetInvalidInstruction() => state.flags |= StateFlags.IsInvalid;

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		internal void DecodeTable(OpCodeHandler[] table, ref Instruction instruction) => DecodeTable(table[(int)ReadByte()], ref instruction);

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		void DecodeTable(OpCodeHandler handler, ref Instruction instruction) {
			if (handler.HasModRM) {
				uint m = ReadByte();
				state.modrm = m;
				state.mod = m >> 6;
				state.reg = (m >> 3) & 7;
				state.rm = m & 7;
			}
			handler.Decode(this, ref instruction);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		internal void ReadModRM() {
			uint m = ReadByte();
			state.modrm = m;
			state.mod = m >> 6;
			state.reg = (m >> 3) & 7;
			state.rm = m & 7;
		}

		internal void VEX2(ref Instruction instruction) {
			if ((((uint)(state.flags & StateFlags.HasRex) | (uint)state.mandatoryPrefix) & invalidCheckMask) != 0)
				SetInvalidInstruction();
			// Undo what Decode() did if it got a REX prefix
			state.flags &= ~StateFlags.W;
			state.extraIndexRegisterBase = 0;
			state.extraBaseRegisterBase = 0;

#if DEBUG
			state.flags |= (StateFlags)EncodingKind.VEX;
#endif
			uint b = state.modrm;
			if (is64Mode)
				state.extraRegisterBase = ((b & 0x80) >> 4) ^ 8;
			// Bit 6 can only be 1 if it's 16/32-bit mode, so we don't need to change the mask
			state.vvvv = (~b >> 3) & 0x0F;

			Static.Assert((int)VectorLength.L128 == 0 ? 0 : -1);
			Static.Assert((int)VectorLength.L256 == 1 ? 0 : -1);
			state.vectorLength = (b >> 2) & 1;

			Static.Assert((int)MandatoryPrefixByte.None == 0 ? 0 : -1);
			Static.Assert((int)MandatoryPrefixByte.P66 == 1 ? 0 : -1);
			Static.Assert((int)MandatoryPrefixByte.PF3 == 2 ? 0 : -1);
			Static.Assert((int)MandatoryPrefixByte.PF2 == 3 ? 0 : -1);
			state.mandatoryPrefix = (MandatoryPrefixByte)(b & 3);

			DecodeTable(handlers_VEX_0FXX, ref instruction);
		}

		internal void VEX3(ref Instruction instruction) {
			if ((((uint)(state.flags & StateFlags.HasRex) | (uint)state.mandatoryPrefix) & invalidCheckMask) != 0)
				SetInvalidInstruction();
			// Undo what Decode() did if it got a REX prefix
			state.flags &= ~StateFlags.W;

#if DEBUG
			state.flags |= (StateFlags)EncodingKind.VEX;
#endif
			uint b1 = state.modrm;
			uint b2 = ReadByte();

			Static.Assert((int)StateFlags.W == 0x80 ? 0 : -1);
			state.flags |= (StateFlags)(b2 & 0x80);

			Static.Assert((int)VectorLength.L128 == 0 ? 0 : -1);
			Static.Assert((int)VectorLength.L256 == 1 ? 0 : -1);
			state.vectorLength = (b2 >> 2) & 1;

			Static.Assert((int)MandatoryPrefixByte.None == 0 ? 0 : -1);
			Static.Assert((int)MandatoryPrefixByte.P66 == 1 ? 0 : -1);
			Static.Assert((int)MandatoryPrefixByte.PF3 == 2 ? 0 : -1);
			Static.Assert((int)MandatoryPrefixByte.PF2 == 3 ? 0 : -1);
			state.mandatoryPrefix = (MandatoryPrefixByte)(b2 & 3);

			if (is64Mode) {
				state.vvvv = (~b2 >> 3) & 0x0F;
				uint b1x = ~b1;
				state.extraRegisterBase = (b1x >> 4) & 8;
				state.extraIndexRegisterBase = (b1x >> 3) & 8;
				state.extraBaseRegisterBase = (b1x >> 2) & 8;
			}
			else
				state.vvvv = (~b2 >> 3) & 0x07;

			int table = (int)(b1 & 0x1F);
			if (table == 1)
				DecodeTable(handlers_VEX_0FXX, ref instruction);
			else if (table == 2)
				DecodeTable(handlers_VEX_0F38XX, ref instruction);
			else if (table == 3)
				DecodeTable(handlers_VEX_0F3AXX, ref instruction);
			else
				SetInvalidInstruction();
		}

		internal void XOP(ref Instruction instruction) {
			if ((((uint)(state.flags & StateFlags.HasRex) | (uint)state.mandatoryPrefix) & invalidCheckMask) != 0)
				SetInvalidInstruction();
			// Undo what Decode() did if it got a REX prefix
			state.flags &= ~StateFlags.W;

#if DEBUG
			state.flags |= (StateFlags)EncodingKind.XOP;
#endif
			uint b1 = state.modrm;
			uint b2 = ReadByte();

			Static.Assert((int)StateFlags.W == 0x80 ? 0 : -1);
			state.flags |= (StateFlags)(b2 & 0x80);

			Static.Assert((int)VectorLength.L128 == 0 ? 0 : -1);
			Static.Assert((int)VectorLength.L256 == 1 ? 0 : -1);
			state.vectorLength = (b2 >> 2) & 1;

			Static.Assert((int)MandatoryPrefixByte.None == 0 ? 0 : -1);
			Static.Assert((int)MandatoryPrefixByte.P66 == 1 ? 0 : -1);
			Static.Assert((int)MandatoryPrefixByte.PF3 == 2 ? 0 : -1);
			Static.Assert((int)MandatoryPrefixByte.PF2 == 3 ? 0 : -1);
			state.mandatoryPrefix = (MandatoryPrefixByte)(b2 & 3);

			if (is64Mode) {
				state.vvvv = (~b2 >> 3) & 0x0F;
				uint b1x = ~b1;
				state.extraRegisterBase = (b1x >> 4) & 8;
				state.extraIndexRegisterBase = (b1x >> 3) & 8;
				state.extraBaseRegisterBase = (b1x >> 2) & 8;
			}
			else
				state.vvvv = (~b2 >> 3) & 0x07;

			int table = (int)(b1 & 0x1F);
			if (table == 8)
				DecodeTable(handlers_XOP8, ref instruction);
			else if (table == 9)
				DecodeTable(handlers_XOP9, ref instruction);
			else if (table == 10)
				DecodeTable(handlers_XOPA, ref instruction);
			else
				SetInvalidInstruction();
		}

		internal void EVEX_MVEX(ref Instruction instruction) {
			if ((((uint)(state.flags & StateFlags.HasRex) | (uint)state.mandatoryPrefix) & invalidCheckMask) != 0)
				SetInvalidInstruction();
			// Undo what Decode() did if it got a REX prefix
			state.flags &= ~StateFlags.W;

			uint p0 = state.modrm;
			uint p1 = ReadByte();
			uint p2 = ReadByte();

			if ((p1 & 4) != 0) {
				if ((p0 & 0x0C) == 0) {
#if DEBUG
					state.flags |= (StateFlags)EncodingKind.EVEX;
#endif

					Static.Assert((int)MandatoryPrefixByte.None == 0 ? 0 : -1);
					Static.Assert((int)MandatoryPrefixByte.P66 == 1 ? 0 : -1);
					Static.Assert((int)MandatoryPrefixByte.PF3 == 2 ? 0 : -1);
					Static.Assert((int)MandatoryPrefixByte.PF2 == 3 ? 0 : -1);
					state.mandatoryPrefix = (MandatoryPrefixByte)(p1 & 3);

					Static.Assert((int)StateFlags.W == 0x80 ? 0 : -1);
					state.flags |= (StateFlags)(p1 & 0x80);

					uint aaa = p2 & 7;
					state.aaa = aaa;
					instruction.InternalOpMask = aaa;
					if ((p2 & 0x80) != 0) {
						// invalid if aaa == 0 and if we check for invalid instructions (it's all 1s)
						if ((aaa ^ invalidCheckMask) == uint.MaxValue)
							SetInvalidInstruction();
						state.flags |= StateFlags.z;
						instruction.InternalSetZeroingMasking();
					}

					Static.Assert((int)StateFlags.b == 0x10 ? 0 : -1);
					state.flags |= (StateFlags)(p2 & 0x10);

					Static.Assert((int)VectorLength.L128 == 0 ? 0 : -1);
					Static.Assert((int)VectorLength.L256 == 1 ? 0 : -1);
					Static.Assert((int)VectorLength.L512 == 2 ? 0 : -1);
					Static.Assert((int)VectorLength.Unknown == 3 ? 0 : -1);
					state.vectorLength = (p2 >> 5) & 3;

					if (is64Mode) {
						state.vvvv = (~p1 >> 3) & 0x0F;
						uint tmp = (~p2 & 8) << 1;
						state.vvvv += tmp;
						state.extraIndexRegisterBaseVSIB = tmp;
						uint p0x = ~p0;
						state.extraRegisterBase = (p0x >> 4) & 8;
						state.extraIndexRegisterBase = (p0x & 0x40) >> 3;
						state.extraBaseRegisterBaseEVEX = (p0x & 0x40) >> 2;
						state.extraBaseRegisterBase = (p0x >> 2) & 8;
						state.extraRegisterBaseEVEX = p0x & 0x10;
					}
					else
						state.vvvv = (~p1 >> 3) & 0x07;

					int table = (int)(p0 & 3);
					if (table == 1)
						DecodeTable(handlers_EVEX_0FXX, ref instruction);
					else if (table == 2)
						DecodeTable(handlers_EVEX_0F38XX, ref instruction);
					else if (table == 3)
						DecodeTable(handlers_EVEX_0F3AXX, ref instruction);
					else
						SetInvalidInstruction();
				}
				else
					SetInvalidInstruction();
			}
			else {
				//TODO: Support deprecated MVEX instructions: https://github.com/0xd4d/iced/issues/2
				SetInvalidInstruction();
			}
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		internal Register ReadOpSegReg() {
			uint reg = state.reg;
			if (reg < 6)
				return Register.ES + (int)reg;
			state.flags |= StateFlags.IsInvalid;
			return Register.None;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		internal void ReadOpMem(ref Instruction instruction) {
			Debug.Assert(state.Encoding != EncodingKind.EVEX);
			if (state.addressSize == OpSize.Size64)
				ReadOpMem32Or64(ref instruction, Register.RAX, Register.RAX, TupleType.None, false);
			else if (state.addressSize == OpSize.Size32)
				ReadOpMem32Or64(ref instruction, Register.EAX, Register.EAX, TupleType.None, false);
			else
				ReadOpMem16(ref instruction, TupleType.None);
		}

		// All MPX instructions in 64-bit mode force 64-bit addressing, and
		// all MPX instructions in 16/32-bit mode require 32-bit addressing
		// (see SDM Vol 1, 17.5.1 Intel MPX and Operating Modes)
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		internal void ReadOpMem_MPX(ref Instruction instruction) {
			Debug.Assert(state.Encoding != EncodingKind.EVEX);
			if (is64Mode) {
				state.addressSize = OpSize.Size64;
				ReadOpMem32Or64(ref instruction, Register.RAX, Register.RAX, TupleType.None, false);
			}
			else if (state.addressSize == OpSize.Size32)
				ReadOpMem32Or64(ref instruction, Register.EAX, Register.EAX, TupleType.None, false);
			else {
				ReadOpMem16(ref instruction, TupleType.None);
				if (invalidCheckMask != 0)
					SetInvalidInstruction();
			}
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		internal void ReadOpMem(ref Instruction instruction, TupleType tupleType) {
			Debug.Assert(state.Encoding == EncodingKind.EVEX);
			if (state.addressSize == OpSize.Size64)
				ReadOpMem32Or64(ref instruction, Register.RAX, Register.RAX, tupleType, false);
			else if (state.addressSize == OpSize.Size32)
				ReadOpMem32Or64(ref instruction, Register.EAX, Register.EAX, tupleType, false);
			else
				ReadOpMem16(ref instruction, tupleType);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		internal void ReadOpMem_VSIB(ref Instruction instruction, Register vsibIndex, TupleType tupleType) {
			bool isValid;
			if (state.addressSize == OpSize.Size64)
				isValid = ReadOpMem32Or64(ref instruction, Register.RAX, vsibIndex, tupleType, true);
			else if (state.addressSize == OpSize.Size32)
				isValid = ReadOpMem32Or64(ref instruction, Register.EAX, vsibIndex, tupleType, true);
			else {
				ReadOpMem16(ref instruction, tupleType);
				isValid = false;
			}
			if (invalidCheckMask != 0 && !isValid)
				SetInvalidInstruction();
		}

		readonly struct RegInfo2 {
			public readonly Register baseReg;
			public readonly Register indexReg;
			public RegInfo2(Register baseReg, Register indexReg) {
				this.baseReg = baseReg;
				this.indexReg = indexReg;
			}
			public void Deconstruct(out Register baseReg, out Register indexReg) {
				baseReg = this.baseReg;
				indexReg = this.indexReg;
			}
		}
		static readonly RegInfo2[] s_memRegs16 = new RegInfo2[8] {
			new RegInfo2(Register.BX, Register.SI),
			new RegInfo2(Register.BX, Register.DI),
			new RegInfo2(Register.BP, Register.SI),
			new RegInfo2(Register.BP, Register.DI),
			new RegInfo2(Register.SI, Register.None),
			new RegInfo2(Register.DI, Register.None),
			new RegInfo2(Register.BP, Register.None),
			new RegInfo2(Register.BX, Register.None),
		};
		void ReadOpMem16(ref Instruction instruction, TupleType tupleType) {
			Debug.Assert(state.addressSize == OpSize.Size16);
			var (baseReg, indexReg) = memRegs16[(int)state.rm];
			switch ((int)state.mod) {
			case 0:
				if (state.rm == 6) {
					instruction.InternalSetMemoryDisplSize(2);
					displIndex = state.instructionLength;
					instruction.MemoryDisplacement = ReadUInt16();
					baseReg = Register.None;
					Debug.Assert(indexReg == Register.None);
				}
				break;

			case 1:
				instruction.InternalSetMemoryDisplSize(1);
				displIndex = state.instructionLength;
				if (tupleType == TupleType.None)
					instruction.MemoryDisplacement = (ushort)(sbyte)ReadByte();
				else
					instruction.MemoryDisplacement = (ushort)(GetDisp8N(tupleType) * (uint)(sbyte)ReadByte());
				break;

			default:
				Debug.Assert(state.mod == 2);
				instruction.InternalSetMemoryDisplSize(2);
				displIndex = state.instructionLength;
				instruction.MemoryDisplacement = ReadUInt16();
				break;
			}

			instruction.InternalMemoryBase = baseReg;
			instruction.InternalMemoryIndex = indexReg;
		}

		// Returns true if the SIB byte was read
		bool ReadOpMem32Or64(ref Instruction instruction, Register baseReg, Register indexReg, TupleType tupleType, bool isVsib) {
			Debug.Assert(state.addressSize == OpSize.Size32 || state.addressSize == OpSize.Size64);
			uint sib;
			uint displSizeScale, displ;
			switch ((int)state.mod) {
			case 0:
				if (state.rm == 4) {
					sib = ReadByte();
					displSizeScale = 0;
					displ = 0;
					break;
				}
				else if (state.rm == 5) {
					if (state.addressSize == OpSize.Size64)
						instruction.InternalSetMemoryDisplSize(4);
					else
						instruction.InternalSetMemoryDisplSize(3);
					displIndex = state.instructionLength;
					instruction.MemoryDisplacement = ReadUInt32();
					if (is64Mode) {
						if (state.addressSize == OpSize.Size64)
							instruction.InternalMemoryBase = Register.RIP;
						else
							instruction.InternalMemoryBase = Register.EIP;
					}
					return false;
				}
				else {
					Debug.Assert(0 <= state.rm && state.rm <= 7 && state.rm != 4 && state.rm != 5);
					instruction.InternalMemoryBase = (int)(state.extraBaseRegisterBase + state.rm) + baseReg;
					return false;
				}

			case 1:
				if (state.rm == 4) {
					sib = ReadByte();
					displSizeScale = 1;
					displIndex = state.instructionLength;
					if (tupleType == TupleType.None)
						displ = (uint)(sbyte)ReadByte();
					else
						displ = GetDisp8N(tupleType) * (uint)(sbyte)ReadByte();
					break;
				}
				else {
					Debug.Assert(0 <= state.rm && state.rm <= 7 && state.rm != 4);
					instruction.InternalSetMemoryDisplSize(1);
					displIndex = state.instructionLength;
					if (tupleType == TupleType.None)
						instruction.MemoryDisplacement = (uint)(sbyte)ReadByte();
					else
						instruction.MemoryDisplacement = GetDisp8N(tupleType) * (uint)(sbyte)ReadByte();
					instruction.InternalMemoryBase = (int)(state.extraBaseRegisterBase + state.rm) + baseReg;
					return false;
				}

			default:
				Debug.Assert(state.mod == 2);
				if (state.rm == 4) {
					sib = ReadByte();
					displSizeScale = state.addressSize == OpSize.Size64 ? 4U : 3;
					displIndex = state.instructionLength;
					displ = ReadUInt32();
					break;
				}
				else {
					Debug.Assert(0 <= state.rm && state.rm <= 7 && state.rm != 4);
					if (state.addressSize == OpSize.Size64)
						instruction.InternalSetMemoryDisplSize(4);
					else
						instruction.InternalSetMemoryDisplSize(3);
					displIndex = state.instructionLength;
					instruction.MemoryDisplacement = ReadUInt32();
					instruction.InternalMemoryBase = (int)(state.extraBaseRegisterBase + state.rm) + baseReg;
					return false;
				}
			}

			uint index = ((sib >> 3) & 7) + state.extraIndexRegisterBase;
			uint @base = sib & 7;

			instruction.InternalMemoryIndexScale = (int)(sib >> 6);
			if (!isVsib) {
				if (index != 4)
					instruction.InternalMemoryIndex = (int)index + indexReg;
			}
			else
				instruction.InternalMemoryIndex = (int)(index + state.extraIndexRegisterBaseVSIB) + indexReg;

			if (@base == 5 && state.mod == 0) {
				if (state.addressSize == OpSize.Size64)
					instruction.InternalSetMemoryDisplSize(4);
				else
					instruction.InternalSetMemoryDisplSize(3);
				displIndex = state.instructionLength;
				instruction.MemoryDisplacement = ReadUInt32();
			}
			else {
				instruction.InternalMemoryBase = (int)(@base + state.extraBaseRegisterBase) + baseReg;
				instruction.InternalSetMemoryDisplSize(displSizeScale);
				instruction.MemoryDisplacement = displ;
			}
			return true;
		}

		uint GetDisp8N(TupleType tupleType) {
			switch (tupleType) {
			case TupleType.None:
				return 1;

			case TupleType.Full_128:
				if ((state.flags & StateFlags.b) != 0)
					return (state.flags & StateFlags.W) != 0 ? 8U : 4;
				return 16;

			case TupleType.Full_256:
				if ((state.flags & StateFlags.b) != 0)
					return (state.flags & StateFlags.W) != 0 ? 8U : 4;
				return 32;

			case TupleType.Full_512:
				if ((state.flags & StateFlags.b) != 0)
					return (state.flags & StateFlags.W) != 0 ? 8U : 4;
				return 64;

			case TupleType.Half_128:
				return (state.flags & StateFlags.b) != 0 ? 4U : 8;

			case TupleType.Half_256:
				return (state.flags & StateFlags.b) != 0 ? 4U : 16;

			case TupleType.Half_512:
				return (state.flags & StateFlags.b) != 0 ? 4U : 32;

			case TupleType.Full_Mem_128:
				return 16;

			case TupleType.Full_Mem_256:
				return 32;

			case TupleType.Full_Mem_512:
				return 64;

			case TupleType.Tuple1_Scalar:
				return (state.flags & StateFlags.W) != 0 ? 8U : 4;

			case TupleType.Tuple1_Scalar_1:
				return 1;

			case TupleType.Tuple1_Scalar_2:
				return 2;

			case TupleType.Tuple1_Scalar_4:
				return 4;

			case TupleType.Tuple1_Scalar_8:
				return 8;

			case TupleType.Tuple1_Fixed_4:
				return 4;

			case TupleType.Tuple1_Fixed_8:
				return 8;

			case TupleType.Tuple2:
				return (state.flags & StateFlags.W) != 0 ? 16U : 8;

			case TupleType.Tuple4:
				return (state.flags & StateFlags.W) != 0 ? 32U : 16;

			case TupleType.Tuple8:
				Debug.Assert((state.flags & StateFlags.W) == 0);
				return 32;

			case TupleType.Tuple1_4X:
				return 16;

			case TupleType.Half_Mem_128:
				return 8;

			case TupleType.Half_Mem_256:
				return 16;

			case TupleType.Half_Mem_512:
				return 32;

			case TupleType.Quarter_Mem_128:
				return 4;

			case TupleType.Quarter_Mem_256:
				return 8;

			case TupleType.Quarter_Mem_512:
				return 16;

			case TupleType.Eighth_Mem_128:
				return 2;

			case TupleType.Eighth_Mem_256:
				return 4;

			case TupleType.Eighth_Mem_512:
				return 8;

			case TupleType.Mem128:
				return 16;

			case TupleType.MOVDDUP_128:
				return 8;

			case TupleType.MOVDDUP_256:
				return 32;

			case TupleType.MOVDDUP_512:
				return 64;

			default:
				Debug.Fail($"Unreachable code");
				return 0;
			}
		}

		/// <summary>
		/// Gets the offsets of the constants (memory displacement and immediate) in the decoded instruction.
		/// The caller can check if there are any relocations at those addresses.
		/// </summary>
		/// <param name="instruction">The latest instruction that was decoded by this decoder</param>
		/// <returns></returns>
		public ConstantOffsets GetConstantOffsets(in Instruction instruction) {
			ConstantOffsets constantOffsets = default;

			int displSize = instruction.MemoryDisplSize;
			if (displSize != 0) {
				constantOffsets.DisplacementOffset = (byte)displIndex;
				if (displSize == 8 && (state.flags & StateFlags.Addr64) == 0)
					constantOffsets.DisplacementSize = 4;
				else
					constantOffsets.DisplacementSize = (byte)displSize;
			}

			if ((state.flags & StateFlags.NoImm) == 0) {
				int extraImmSub = 0;
				for (int i = instruction.OpCount - 1; i >= 0; i--) {
					switch (instruction.GetOpKind(i)) {
					case OpKind.Immediate8:
					case OpKind.Immediate8to16:
					case OpKind.Immediate8to32:
					case OpKind.Immediate8to64:
						constantOffsets.ImmediateOffset = (byte)(instruction.Length - extraImmSub - 1);
						constantOffsets.ImmediateSize = 1;
						goto after_imm_loop;

					case OpKind.Immediate16:
						constantOffsets.ImmediateOffset = (byte)(instruction.Length - extraImmSub - 2);
						constantOffsets.ImmediateSize = 2;
						goto after_imm_loop;

					case OpKind.Immediate32:
					case OpKind.Immediate32to64:
						constantOffsets.ImmediateOffset = (byte)(instruction.Length - extraImmSub - 4);
						constantOffsets.ImmediateSize = 4;
						goto after_imm_loop;

					case OpKind.Immediate64:
						constantOffsets.ImmediateOffset = (byte)(instruction.Length - extraImmSub - 8);
						constantOffsets.ImmediateSize = 8;
						goto after_imm_loop;

					case OpKind.Immediate8_2nd:
						constantOffsets.ImmediateOffset2 = (byte)(instruction.Length - 1);
						constantOffsets.ImmediateSize2 = 1;
						extraImmSub = 1;
						break;

					case OpKind.NearBranch16:
						if ((state.flags & StateFlags.BranchImm8) != 0) {
							constantOffsets.ImmediateOffset = (byte)(instruction.Length - 1);
							constantOffsets.ImmediateSize = 1;
						}
						else if ((state.flags & StateFlags.Xbegin) == 0) {
							constantOffsets.ImmediateOffset = (byte)(instruction.Length - 2);
							constantOffsets.ImmediateSize = 2;
						}
						else {
							Debug.Assert((state.flags & StateFlags.Xbegin) != 0);
							if (state.operandSize != OpSize.Size16) {
								constantOffsets.ImmediateOffset = (byte)(instruction.Length - 4);
								constantOffsets.ImmediateSize = 4;
							}
							else {
								constantOffsets.ImmediateOffset = (byte)(instruction.Length - 2);
								constantOffsets.ImmediateSize = 2;
							}
						}
						break;

					case OpKind.NearBranch32:
					case OpKind.NearBranch64:
						if ((state.flags & StateFlags.BranchImm8) != 0) {
							constantOffsets.ImmediateOffset = (byte)(instruction.Length - 1);
							constantOffsets.ImmediateSize = 1;
						}
						else if ((state.flags & StateFlags.Xbegin) == 0) {
							constantOffsets.ImmediateOffset = (byte)(instruction.Length - 4);
							constantOffsets.ImmediateSize = 4;
						}
						else {
							Debug.Assert((state.flags & StateFlags.Xbegin) != 0);
							if (state.operandSize != OpSize.Size16) {
								constantOffsets.ImmediateOffset = (byte)(instruction.Length - 4);
								constantOffsets.ImmediateSize = 4;
							}
							else {
								constantOffsets.ImmediateOffset = (byte)(instruction.Length - 2);
								constantOffsets.ImmediateSize = 2;
							}
						}
						break;

					case OpKind.FarBranch16:
						constantOffsets.ImmediateOffset = (byte)(instruction.Length - (2 + 2));
						constantOffsets.ImmediateSize = 2;
						constantOffsets.ImmediateOffset2 = (byte)(instruction.Length - 2);
						constantOffsets.ImmediateSize2 = 2;
						break;

					case OpKind.FarBranch32:
						constantOffsets.ImmediateOffset = (byte)(instruction.Length - (4 + 2));
						constantOffsets.ImmediateSize = 4;
						constantOffsets.ImmediateOffset2 = (byte)(instruction.Length - 2);
						constantOffsets.ImmediateSize2 = 2;
						break;
					}
				}
			}
after_imm_loop:

			return constantOffsets;
		}
	}
}
#endif
