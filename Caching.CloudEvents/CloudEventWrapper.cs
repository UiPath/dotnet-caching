using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CloudNative.CloudEvents;
using UiPath.Platform.Caching.Broadcast;

namespace UiPath.Platform.Caching.CloudEvents;

public abstract class CloudEventWrapper : IPubSubEvent
{
    public abstract string? Id { get; set; }

    public abstract Uri? Source { get; set; }

    public abstract bool IsValid();


    public abstract CloudEvent CloudEvent { get; }
}
