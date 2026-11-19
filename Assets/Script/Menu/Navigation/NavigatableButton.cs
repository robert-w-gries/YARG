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

        /// <summary>
        /// True if a click on this button calls a method with one of these names. This method examines only
        /// the methods that the Inspector connects to the click event. The pause menus use this method to find
        /// buttons by their action (for example, "Restart"). The buttons have no other identifier.
        /// </summary>
        public bool CallsAnyMethod(params string[] methodNames)
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