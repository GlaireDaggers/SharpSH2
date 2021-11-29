using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace SharpSh2.Test
{
	// TODO: more tests

	[TestClass]
	public class ShTests
	{
		[TestMethod]
		public void BusTest()
		{
			byte[] romData = Utils.LoadEmbeddedRom("Resources.bios.bin");

			SimpleBusMapper bus = new SimpleBusMapper();
			LinearReadOnlyMemory rom = new LinearReadOnlyMemory(romData);
			LinearMemory ram = new LinearMemory(0x200000);
			bus.Map(rom, 0, 0x200000);
			bus.Map(ram, 0x01000000, 0x200000);

			IBus d0 = bus.GetDeviceAt(0, out uint rom_startAddr, out uint rom_endAddr);
			IBus d1 = bus.GetDeviceAt(0x01000000, out uint ram_startAddr, out uint ram_endAddr);

			Assert.IsTrue(d0 == rom);
			Assert.IsTrue(rom_startAddr == 0);
			Assert.IsTrue(rom_endAddr == 0x00200000);
			Assert.IsTrue(d1 == ram);
			Assert.IsTrue(ram_startAddr == 0x01000000);
			Assert.IsTrue(ram_endAddr == 0x01200000);
		}

		[TestMethod]
		public void ResetTest()
		{
			byte[] romData = Utils.LoadEmbeddedRom("Resources.bios.bin");

			SimpleBusMapper bus = new SimpleBusMapper();
			LinearReadOnlyMemory rom = new LinearReadOnlyMemory(romData);
			LinearMemory ram = new LinearMemory(0x200000);
			bus.Map(rom, 0, 0x200000);
			bus.Map(ram, 0x01000000, 0x200000);

			Sh2Cpu cpu = new Sh2Cpu(bus);
			cpu.PowerOn();
		}

		[TestMethod]
		public void Sandbox()
		{
			byte[] romData = Utils.LoadEmbeddedRom("Resources.bios.bin");

			SimpleBusMapper bus = new SimpleBusMapper();
			var cpu = new Sh2Cpu(bus);

			LinearReadOnlyMemory rom = new LinearReadOnlyMemory(romData);
			LinearMemory ram = new LinearMemory(0x200000);
			SerialDebug dbg = new SerialDebug(cpu, 0);
			bus.Map(rom, 0, 0x200000);
			bus.Map(ram, 0x01000000, 0x200000);
			bus.Map(dbg, 0x0F000000, 4);

			cpu.PowerOn();

			while (cpu.State == CpuState.ProgramExecution)
			{
				cpu.Cycle();
			}

			Assert.IsTrue(cpu.State == CpuState.Sleep);
		}

		[TestMethod]
		public void TestMultiplyAlgorithm()
		{
			for (int n = -1024; n < 1024; n++)
			{
				for (int m = -1024; m < 1024; m++)
				{
					CustomMulS((uint)n, (uint)m, out var custom_mach, out var custom_macl);
					MameMulS((uint)n, (uint)m, out var mame_mach, out var mame_macl);

					Assert.AreEqual(mame_mach, custom_mach);
					Assert.AreEqual(mame_macl, custom_macl);
				}
			}

			// one last one
			unchecked
			{
				CustomMulS((uint)int.MaxValue, (uint)int.MinValue, out var custom_mach2, out var custom_macl2);
				MameMulS((uint)int.MaxValue, (uint)int.MinValue, out var mame_mach2, out var mame_macl2);

				Assert.AreEqual(mame_mach2, custom_mach2);
				Assert.AreEqual(mame_macl2, custom_macl2);
			}
		}

		#region Utility Methods

		private void CustomMulS(uint n, uint m, out uint mach, out uint macl)
		{
			long tempn = (int)n;
			long tempm = (int)m;

			ulong result = (ulong)(tempn * tempm);

			macl = (uint)(result & 0xFFFFFFFF);
			mach = (uint)(result >> 32) & 0xFFFFFFFF;
		}

		private void MameMulS(uint n, uint m, out uint mach, out uint macl)
		{
			uint RnL, RnH, RmL, RmH, Res0, Res1, Res2;
			uint temp0, temp1, temp2, temp3;
			int tempm, tempn, fnLmL;

			tempn = (int)n;
			tempm = (int)m;

			if (tempn < 0)
				tempn = 0 - tempn;

			if (tempm < 0)
				tempm = 0 - tempm;

			if ((int)(n ^ m) < 0)
				fnLmL = -1;
			else
				fnLmL = 0;

			temp1 = (uint)tempn;
			temp2 = (uint)tempm;
			RnL = temp1 & 0x0000ffff;
			RnH = (temp1 >> 16) & 0x0000ffff;
			RmL = temp2 & 0x0000ffff;
			RmH = (temp2 >> 16) & 0x0000ffff;
			temp0 = RmL * RnL;
			temp1 = RmH * RnL;
			temp2 = RmL * RnH;
			temp3 = RmH * RnH;
			Res2 = 0;
			Res1 = temp1 + temp2;
			if (Res1 < temp1)
				Res2 += 0x00010000;
			temp1 = (Res1 << 16) & 0xffff0000;
			Res0 = temp0 + temp1;
			if (Res0 < temp0)
				Res2++;
			Res2 = Res2 + ((Res1 >> 16) & 0x0000ffff) + temp3;
			if (fnLmL < 0)
			{
				Res2 = ~Res2;
				if (Res0 == 0)
					Res2++;
				else
					Res0 = (~Res0) + 1;
			}
			
			mach = Res2;
			macl = Res0;
		}

		#endregion
	}
}
