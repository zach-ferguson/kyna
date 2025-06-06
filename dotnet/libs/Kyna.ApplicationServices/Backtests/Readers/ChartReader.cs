﻿using Kyna.Analysis.Technical.Charts;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Kyna.ApplicationServices.Backtests.Readers;

internal abstract class ChartReader
{
    public abstract IEnumerable<TradeSignal> Read(Chart chart);
}
