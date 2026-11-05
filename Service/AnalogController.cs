using ParallelReactor.Hardware;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ParallelReactor.Service
{
    public sealed class AnalogController
    {
        private readonly AnalogModule _analog;

        public AnalogController(AnalogModule analog)
        {
            _analog = analog;
        }

        public event Action<string> CommError;


        public async Task<double[]> GetAnalogValuesAsync()
        {
            try
            {
                return await _analog.ReadScaledAsync(0, 600, true);
            }
            catch (Exception e)
            {
                CommError?.Invoke(e.Message);
                return Array.Empty<double>();
            }
        }
    }
}
