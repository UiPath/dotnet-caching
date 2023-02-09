using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace UiPath.Platform.Caching.Broadcast;
public interface IPubSubEvent
{
    string? Id { get; }
    Uri? Source { get; }
    bool IsValid();
}
