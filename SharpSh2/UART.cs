using System;
using System.Collections.Generic;
using System.Text;

namespace SharpSh2
{
	/// <summary>
	/// Represents a simple UART-like device which can be used to read and write sequences of bytes
	/// </summary>
	public class UART : IBus
	{
		#region Fields

		private Sh2Cpu _cpu;
		private int _irq;
		private List<byte> _rxBuffer;

		#endregion

		#region Constructor

		public UART(Sh2Cpu cpu, int irq)
		{
			_cpu = cpu;
			_irq = irq;
		}

		#endregion

		#region API

		/// <summary>
		/// Append data to this device's read queue
		/// </summary>
		public void Write(byte[] data)
		{
			_rxBuffer.AddRange(data);
			_cpu.IRQ(_irq);
		}

		/// <summary>
		/// Called when this device sends a byte over its TX line
		/// </summary>
		protected virtual void OnTxWrite(byte data)
		{
		}

		private byte Dequeue(List<byte> queue)
		{
			byte val = queue[0];
			queue.RemoveAt(0);
			return val;
		}

		#endregion

		#region IBus

		public ushort Read16(uint addr)
		{
			return 0;
		}

		public uint Read32(uint addr)
		{
			return 0;
		}

		public byte Read8(uint addr)
		{
			switch (addr)
			{
				// UART_TX_READY
				case 0:
					return 1;
				// UART_RX_READY
				case 1:
					return (byte)(_rxBuffer.Count > 0 ? 1 : 0);
				// UART_TX_DATA
				case 2:
					return 0;
				// UART_RX_DATA
				case 3:
					if (_rxBuffer.Count == 0) return 0;
					return Dequeue(_rxBuffer);
				default:
					return 0;
			}
		}

		public void Write16(uint addr, ushort value)
		{
		}

		public void Write32(uint addr, uint value)
		{
		}

		public void Write8(uint addr, byte value)
		{
			// UART_TX_DATA
			if (addr == 2)
			{
				OnTxWrite(value);
			}
		}

		#endregion
	}

	/// <summary>
	/// A UART device which logs text output to Console.Write
	/// </summary>
	public class SerialDebug : UART
	{
		private List<byte> _rxbuffer;

		public SerialDebug(Sh2Cpu cpu, int irq) : base(cpu, irq)
		{
			_rxbuffer = new List<byte>();
		}

		protected override void OnTxWrite(byte data)
		{
			base.OnTxWrite(data);

			if (data != 0)
				_rxbuffer.Add(data);
			
			if ((char)data == '\0' || (char)data == '\n')
			{
				// flush output
				string str = Encoding.UTF8.GetString(_rxbuffer.ToArray());
				Console.Write(str);
				_rxbuffer.Clear();
			}
		}
	}
}
