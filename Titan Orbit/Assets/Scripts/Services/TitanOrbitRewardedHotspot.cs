using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace TitanOrbit.Services
{
    /// <summary>
    /// Marks a Unity UI button whose click must start an H5 rewarded ad
    /// inside the browser's own pointer event. Google ignores <c>showAdFn</c>
    /// when it is called later from the player loop.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TitanOrbitRewardedHotspot : MonoBehaviour
    {
        /// <summary>Watch Ad buttons that are currently enabled.</summary>
        public static readonly List<Button> Live = new List<Button>(8);

        Button _button;

        /// <summary>[UNITY] Cache the button on this object.</summary>
        void Awake()
        {
            _button = GetComponent<Button>();
        }

        /// <summary>[UNITY] The page hit-test only considers enabled buttons.</summary>
        void OnEnable()
        {
            if (_button == null)
                _button = GetComponent<Button>();
            if (_button != null && !Live.Contains(_button))
                Live.Add(_button);
        }

        /// <summary>[UNITY] A hidden Watch Ad button must not steal the next click.</summary>
        void OnDisable()
        {
            if (_button != null)
                Live.Remove(_button);
        }
    }
}
