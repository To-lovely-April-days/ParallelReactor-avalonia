using Avalonia.Threading;
using ParallelReactor.Hardware;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace ParallelReactor.Service
{
    public sealed class IoController
    {
        private readonly IoModule _io;
        private readonly DispatcherTimer _valveStatus;

        private bool _receiving;

        public IoController(IoModule io)
        {
            _io = io;

            //_valveStatus = new DispatcherTimer() { Interval = TimeSpan.FromSeconds(1) };
            //_valveStatus.Tick += (s, e) => _ = ValveStatusLoop();
        }

        /// <summary>
        /// 串口通讯错误事件
        /// </summary>
        public event EventHandler<string> CommError;

        /// <summary>
        /// 阀门状态接收事件
        /// </summary>
        public event EventHandler<bool[]> StatusReceived;

        /// <summary>
        /// 打开/关闭阀门
        /// </summary>
        /// <param name="index">阀门编号</param>
        /// <param name="on">指示阀门是打开还是关闭</param>
        /// <returns></returns>
        public async Task<bool> SetValveAsync(int index, bool on)
        {
            try
            {
                await _io.SetCoilAsync(index, on);
                return true;
            }
            catch (Exception e)
            {
                CommError?.Invoke(this, e.Message);
                return false;
            }
        }

        /// <summary>
        /// 打开/关闭所有阀门
        /// </summary>
        /// <returns></returns>
        public async Task<bool> BatchSetAsync(bool[] values)
        {
            try
            {
                if (values == null || values.Length == 0)
                {
                    throw new ArgumentException("批量设置阀门状态的数据异常", nameof(values));
                }
                await _io.SetAllCoilsAsync(values);
                return true;
            }
            catch (Exception e)
            {
                CommError?.Invoke(this, e.Message);
                return false;
            }
        }

        public async Task<bool[]> BatchReadValveAsync()
        {
            try
            {
                return await _io.ReadCoilsAsync();
            }
            catch (Exception e)
            {
                CommError?.Invoke(this, e.Message);
                return Array.Empty<bool>();
            }
        }

        public async Task ValveStatusLoop()
        {
            if (_receiving)
            {
                return;
            }
            _receiving = true;
            try
            {
                var result = await _io.ReadCoilsAsync();
                StatusReceived?.Invoke(this, result);
            }
            catch (Exception e)
            {
                CommError?.Invoke(this, e.Message);
            }
            _receiving = false;
        }
    }
}
