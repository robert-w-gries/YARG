using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace YARG.Menu.Navigation
{
    public sealed class NavigatableButton : NavigatableBehaviour
    {
        [SerializeField]
        private Button.ButtonClickedEvent _onClick = new();

        public override void OnPointerDown(PointerEventData eventData)
        {
            base.OnPointerDown(eventData);
            Confirm();
        }

        public override void Confirm()
        {
            _onClick.Invoke();
        }

        /// <summary>Whether a click calls a method with one of these names (as set up in the Inspector).</summary>
        public bool Calls(params string[] methodNames)
        {
            for (int i = 0; i < _onClick.GetPersistentEventCount(); i++)
            {
                if (System.Array.IndexOf(methodNames, _onClick.GetPersistentMethodName(i)) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        public void RemoveOnClickListeners()
        {
            _onClick.RemoveAllListeners();
        }

        public void SetOnClickEvent(UnityAction a)
        {
            _onClick.RemoveAllListeners();
            _onClick.AddListener(a);
        }
    }
}