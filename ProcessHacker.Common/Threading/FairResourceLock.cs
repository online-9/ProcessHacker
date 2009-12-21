﻿/*
 * Process Hacker - 
 *   fair resource lock
 * 
 * Copyright (C) 2009 wj32
 * 
 * This file is part of Process Hacker.
 * 
 * Process Hacker is free software; you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * Process Hacker is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with Process Hacker.  If not, see <http://www.gnu.org/licenses/>.
 */

#define DEFER_EVENT_CREATION

using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace ProcessHacker.Common.Threading
{
    /// <summary>
    /// Provides a fast and fair resource (reader-writer) lock.
    /// </summary>
    /// <remarks>
    /// FairResourceLock has slightly more overhead than FastResourceLock, 
    /// but guarantees that waiters will be released in FIFO order and 
    /// provides better ownership conversion functions.
    /// </remarks>
    public unsafe sealed class FairResourceLock : IDisposable, IResourceLock
    {
        #region Constants

        // Lock owned
        private const int LockOwned = 0x1;
        // Waiters present
        private const int LockWaiters = 0x2;

        // Shared owners count
        private const int LockSharedOwnersShift = 2;
        private const int LockSharedOwnersMask = 0x3fffffff;
        private const int LockSharedOwnersIncrement = 0x4;

        private const int WaiterExclusive = 0x1;
        private const int WaiterSpinning = 0x2;

        #endregion

        private unsafe struct WaitBlock
        {
            public static readonly int Size = Marshal.SizeOf(typeof(WaitBlock));

            public WaitBlock* Flink;
            public WaitBlock* Blink;
            public int Flags;
        }

        private enum ListPosition
        {
            /// <summary>
            /// The wait block will be inserted ahead of all other wait blocks.
            /// </summary>
            First,

            /// <summary>
            /// The wait block will be inserted behind all exclusive wait blocks 
            /// but ahead of the first shared wait block (if any).
            /// </summary>
            LastExclusive,

            /// <summary>
            /// The wait block will be inserted behind all other wait blocks.
            /// </summary>
            Last
        }

        private static void InsertHeadList(WaitBlock* listHead, WaitBlock* entry)
        {
            WaitBlock* flink;

            flink = listHead->Flink;
            entry->Flink = flink;
            entry->Blink = listHead;
            flink->Blink = entry;
            listHead->Flink = entry;
        }

        private static void InsertTailList(WaitBlock* listHead, WaitBlock* entry)
        {
            WaitBlock* blink;

            blink = listHead->Blink;
            entry->Flink = listHead;
            entry->Blink = blink;
            blink->Flink = entry;
            listHead->Blink = entry;
        }

        private static bool RemoveEntryList(WaitBlock* entry)
        {
            WaitBlock* blink;
            WaitBlock* flink;

            flink = entry->Flink;
            blink = entry->Blink;
            blink->Flink = flink;
            flink->Blink = blink;

            return flink == blink;
        }

        private static WaitBlock* RemoveHeadList(WaitBlock* listHead)
        {
            WaitBlock* flink;
            WaitBlock* entry;

            entry = listHead->Flink;
            flink = entry->Flink;
            listHead->Flink = flink;
            flink->Blink = listHead;

            return entry;
        }

        private int _value;
        private int _spinCount;
        private IntPtr _wakeEvent;

        private SpinLock _lock;
        private WaitBlock* _waitersListHead;
        private WaitBlock* _firstSharedWaiter;

        /// <summary>
        /// Creates a FairResourceLock.
        /// </summary>
        public FairResourceLock()
            : this(NativeMethods.SpinCount)
        { }

        /// <summary>
        /// Creates a FairResourceLock, specifying a spin count.
        /// </summary>
        /// <param name="spinCount">
        /// The number of times to spin before going to sleep.
        /// </param>
        public FairResourceLock(int spinCount)
        {
            _value = 0;
            _lock = new SpinLock();
            _spinCount = Environment.ProcessorCount != 1 ? spinCount : 0;

            _waitersListHead = (WaitBlock*)Marshal.AllocHGlobal(WaitBlock.Size);
            _waitersListHead->Flink = _waitersListHead;
            _waitersListHead->Blink = _waitersListHead;
            _firstSharedWaiter = _waitersListHead;

#if !DEFER_EVENT_CREATION
            _wakeEvent = this.CreateWakeEvent();
#endif
        }

        ~FairResourceLock()
        {
            this.Dispose(false);
        }

        private void Dispose(bool disposing)
        {
            if (_waitersListHead != null)
            {
                Marshal.FreeHGlobal((IntPtr)_waitersListHead);
                _waitersListHead = null;
            }

            if (_wakeEvent != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(_wakeEvent);
                _wakeEvent = IntPtr.Zero;
            }
        }

        /// <summary>
        /// Disposes resources associated with the FairResourceLock.
        /// </summary>
        public void Dispose()
        {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Gets whether the lock is owned in either 
        /// exclusive or shared mode.
        /// </summary>
        public bool Owned
        {
            get { return (_value & LockOwned) != 0; }
        }

        /// <summary>
        /// Gets the number of shared owners.
        /// </summary>
        public int SharedOwners
        {
            get { return (_value >> LockSharedOwnersShift) & LockSharedOwnersMask; }
        }

        /// <summary>
        /// Gets the number of times to spin before going to sleep.
        /// </summary>
        public int SpinCount
        {
            get { return _spinCount; }
            set { _spinCount = value; }
        }

        /// <summary>
        /// Acquires the lock in exclusive mode, blocking 
        /// if necessary.
        /// </summary>
        /// <remarks>
        /// Exclusive acquires are given precedence over shared 
        /// acquires.
        /// </remarks>
        public void AcquireExclusive()
        {
            this.AcquireExclusive(true);
        }

        /// <summary>
        /// Acquires the lock in exclusive mode, blocking 
        /// if necessary.
        /// </summary>
        /// <param name="sleep">
        /// If true, go to sleep, otherwise busy wait indefinitely.
        /// </param>
        /// <remarks>
        /// Exclusive acquires are given precedence over shared 
        /// acquires.
        /// </remarks>
        private void AcquireExclusive(bool sleep)
        {
            int value;
            int i = 0;

            while (true)
            {
                value = _value;

                // Try to obtain the lock.
                if ((value & LockOwned) == 0)
                {
                    if (Interlocked.CompareExchange(
                        ref _value,
                        value + LockOwned,
                        value
                        ) == value)
                        break;
                }
                else if (i >= _spinCount)
                {
                    // We need to wait.
                    WaitBlock waitBlock;

                    waitBlock.Flags = WaiterExclusive | WaiterSpinning;

                    // Obtain the waiters list lock.
                    _lock.Acquire();

                    try
                    {
                        // Try to set the waiters bit.
                        if (Interlocked.CompareExchange(
                            ref _value,
                            value | LockWaiters,
                            value
                            ) != value)
                        {
                            // Unfortunately we have to go back. This is 
                            // very wasteful since the waiters list lock 
                            // must be released again, but must happen since 
                            // the lock may have been released.
                            continue;
                        }

                        // Put our wait block behind other exclusive waiters but 
                        // in front of all shared waiters.
                        this.InsertWaitBlock(&waitBlock, ListPosition.LastExclusive);
                    }
                    finally
                    {
                        _lock.Release();
                    }

                    this.Block(&waitBlock, sleep);

                    // Go back and try again.
                    continue;
                }

                i++;
            }
        }

        /// <summary>
        /// Acquires the lock in shared mode, blocking 
        /// if necessary.
        /// </summary>
        /// <remarks>
        /// Exclusive acquires are given precedence over shared 
        /// acquires.
        /// </remarks>
        public void AcquireShared()
        {
            this.AcquireShared(true);
        }

        /// <summary>
        /// Acquires the lock in shared mode, blocking 
        /// if necessary.
        /// </summary>
        /// <param name="sleep">
        /// If true, go to sleep, otherwise busy wait indefinitely.
        /// </param>
        /// <remarks>
        /// Exclusive acquires are given precedence over shared 
        /// acquires.
        /// </remarks>
        private void AcquireShared(bool sleep)
        {
            int value;
            int i = 0;

            while (true)
            {
                value = _value;

                // Try to obtain the lock.
                // Note that we don't acquire if there are waiters and 
                // the lock is already owned in shared mode, in order to 
                // give exclusive acquires precedence.
                if (
                    (value & LockOwned) == 0 ||
                    ((value & LockWaiters) == 0 && ((value >> LockSharedOwnersShift) & LockSharedOwnersMask) != 0)
                    )
                {
                    if ((value & LockOwned) == 0)
                    {
                        if (Interlocked.CompareExchange(
                            ref _value,
                            value + LockOwned + LockSharedOwnersIncrement,
                            value
                            ) == value)
                            break;
                    }
                    else
                    {
                        if (Interlocked.CompareExchange(
                            ref _value,
                            value + LockSharedOwnersIncrement,
                            value
                            ) == value)
                            break;
                    }
                }
                else if (i >= _spinCount)
                {
                    // We need to wait.
                    WaitBlock waitBlock;

                    waitBlock.Flags = WaiterSpinning;

                    // Obtain the waiters list lock.
                    _lock.Acquire();

                    try
                    {
                        // Try to set the waiters bit.
                        if (Interlocked.CompareExchange(
                            ref _value,
                            value | LockWaiters,
                            value
                            ) != value)
                        {
                            continue;
                        }

                        // Put our wait block behind other waiters.
                        this.InsertWaitBlock(&waitBlock, ListPosition.Last);

                        // Set the first shared waiter pointer.
                        if (
                            waitBlock.Blink == _waitersListHead ||
                            (waitBlock.Blink->Flags & WaiterExclusive) != 0
                            )
                        {
                            _firstSharedWaiter = &waitBlock;
                        }
                    }
                    finally
                    {
                        _lock.Release();
                    }

                    this.Block(&waitBlock, sleep);

                    // Go back and try again.
                    continue;
                }

                i++;
            }
        }

        /// <summary>
        /// Blocks on a wait block.
        /// </summary>
        /// <param name="waitBlock">The wait block to block on.</param>
        /// <param name="sleep">
        /// If true, go to sleep if not unblocked while spinning, 
        /// otherwise continue spinning.
        /// </param>
        private void Block(WaitBlock* waitBlock, bool sleep)
        {
            int flags;

            // Spin for a while.
            for (int j = 0; j < _spinCount; j++)
            {
                if ((Thread.VolatileRead(ref waitBlock->Flags) & WaiterSpinning) == 0)
                    break;
            }

            if (sleep)
            {
#if DEFER_EVENT_CREATION
                IntPtr wakeEvent;

                wakeEvent = Thread.VolatileRead(ref _wakeEvent);

                if (wakeEvent == IntPtr.Zero)
                {
                    wakeEvent = this.CreateWakeEvent();

                    if (Interlocked.CompareExchange(ref _wakeEvent, wakeEvent, IntPtr.Zero) != IntPtr.Zero)
                        NativeMethods.CloseHandle(wakeEvent);
                }

#endif
                // Clear the spinning flag.
                do
                {
                    flags = waitBlock->Flags;
                } while (Interlocked.CompareExchange(
                    ref waitBlock->Flags,
                    flags & ~WaiterSpinning,
                    flags
                    ) != flags);

                // Go to sleep if necessary.
                if ((flags & WaiterSpinning) != 0)
                {
                    if (NativeMethods.NtWaitForKeyedEvent(
                        _wakeEvent,
                        new IntPtr(waitBlock),
                        false,
                        IntPtr.Zero
                        ) != 0)
                        throw new Exception(Utils.MsgFailedToWaitIndefinitely);
                }
            }
            else
            {
                while (true)
                {
                    if ((Thread.VolatileRead(ref waitBlock->Flags) & WaiterSpinning) == 0)
                        break;

                    if (NativeMethods.SpinEnabled)
                        Thread.SpinWait(SpinCount);
                    else
                        Thread.Sleep(0);
                }
            }
        }

        /// <summary>
        /// Converts the ownership mode from exclusive to shared.
        /// </summary>
        /// <remarks>
        /// This operation is almost the same as releasing then 
        /// acquiring in shared mode, except that exclusive waiters 
        /// are not given a chance to acquire the lock.
        /// </remarks>
        public void ConvertExclusiveToShared()
        {
            int value;

            while (true)
            {
                value = _value;

                if (Interlocked.CompareExchange(
                    ref _value,
                    value + LockSharedOwnersIncrement,
                    value
                    ) == value)
                {
                    if ((value & LockWaiters) != 0)
                        this.WakeShared();

                    break;
                }
            }
        }

        /// <summary>
        /// Converts the ownership mode from shared to exclusive, 
        /// blocking if necessary.
        /// </summary>
        /// <remarks>
        /// This operation is almost the same as releasing then 
        /// acquiring in exclusive mode, except that the caller is 
        /// placed ahead of all other waiters when acquiring.
        /// </remarks>
        public void ConvertSharedToExclusive()
        {
            this.ConvertSharedToExclusive(true);
        }

        /// <summary>
        /// Converts the ownership mode from shared to exclusive.
        /// </summary>
        /// <param name="sleep">
        /// If true, go to sleep, otherwise busy wait indefinitely.
        /// </param>
        /// <remarks>
        /// This operation is almost the same as releasing then 
        /// acquiring in exclusive mode, except that the caller is 
        /// placed ahead of all other waiters when acquiring.
        /// </remarks>
        private void ConvertSharedToExclusive(bool sleep)
        {
            int value;
            int i = 0;

            while (true)
            {
                value = _value;

                // Are we the only shared waiter? If so, acquire in exclusive mode, 
                // otherwise wait.
                if (((value >> LockSharedOwnersShift) & LockSharedOwnersMask) == 1)
                {
                    if (Interlocked.CompareExchange(
                        ref _value,
                        value - LockSharedOwnersIncrement,
                        value
                        ) == value)
                        break;
                }
                else if (i >= _spinCount)
                {
                    // We need to wait.
                    WaitBlock waitBlock;

                    waitBlock.Flags = WaiterExclusive | WaiterSpinning;

                    // Obtain the waiters list lock.
                    _lock.Acquire();

                    try
                    {
                        // Try to set the waiters bit.
                        if (Interlocked.CompareExchange(
                            ref _value,
                            value | LockWaiters,
                            value
                            ) != value)
                        {
                            continue;
                        }

                        // Put our wait block ahead of all other waiters.
                        this.InsertWaitBlock(&waitBlock, ListPosition.First);
                    }
                    finally
                    {
                        _lock.Release();
                    }

                    this.Block(&waitBlock, sleep);

                    // Go back and try again.
                    continue;
                }

                i++;
            }
        }

        /// <summary>
        /// Creates a wake event.
        /// </summary>
        /// <returns>A handle to the keyed event.</returns>
        private IntPtr CreateWakeEvent()
        {
            IntPtr wakeEvent;

            if (NativeMethods.NtCreateKeyedEvent(
                out wakeEvent,
                0x3,
                IntPtr.Zero,
                0
                ) < 0)
                throw new Exception("Failed to create the wake event.");

            return wakeEvent;
        }

        /// <summary>
        /// Inserts a wait block into the waiters list.
        /// </summary>
        /// <param name="waitBlock">The wait block to insert.</param>
        /// <param name="position">Specifies where to insert the wait block.</param>
        private void InsertWaitBlock(WaitBlock* waitBlock, ListPosition position)
        {
            switch (position)
            {
                case ListPosition.First:
                    InsertHeadList(_waitersListHead, waitBlock);
                    break;
                case ListPosition.LastExclusive:
                    InsertTailList(_firstSharedWaiter, waitBlock);
                    break;
                case ListPosition.Last:
                    InsertTailList(_waitersListHead, waitBlock);
                    break;
            }
        }

        /// <summary>
        /// Releases the lock in exclusive mode.
        /// </summary>
        public void ReleaseExclusive()
        {
            int value;

            while (true)
            {
                value = _value;

                if (Interlocked.CompareExchange(
                    ref _value,
                    value - LockOwned,
                    value
                    ) == value)
                {
                    if ((value & LockWaiters) != 0)
                        this.Wake();

                    break;
                }
            }
        }

        /// <summary>
        /// Releases the lock in shared mode.
        /// </summary>
        public void ReleaseShared()
        {
            int value;
            int newValue;

            while (true)
            {
                value = _value;

                if (((value >> LockSharedOwnersShift) & LockSharedOwnersMask) != 1)
                    newValue = value - LockSharedOwnersIncrement;
                else
                    newValue = value - LockOwned - LockSharedOwnersIncrement;

                if (Interlocked.CompareExchange(
                    ref _value,
                    newValue,
                    value
                    ) == value)
                {
                    if ((value & LockWaiters) != 0)
                        this.Wake();

                    break;
                }
            }
        }

        /// <summary>
        /// Acquires the lock in exclusive mode, busy waiting 
        /// if necessary.
        /// </summary>
        /// <remarks>
        /// Exclusive acquires are given precedence over shared 
        /// acquires.
        /// </remarks>
        public void SpinAcquireExclusive()
        {
            this.AcquireExclusive(false);
        }

        /// <summary>
        /// Acquires the lock in shared mode, busy waiting 
        /// if necessary.
        /// </summary>
        /// <remarks>
        /// Exclusive acquires are given precedence over shared 
        /// acquires.
        /// </remarks>
        public void SpinAcquireShared()
        {
            this.AcquireShared(false);
        }

        /// <summary>
        /// Converts the ownership mode from shared to exclusive, 
        /// busy waiting if necessary.
        /// </summary>
        public void SpinConvertSharedToExclusive()
        {
            this.ConvertSharedToExclusive(false);
        }

        /// <summary>
        /// Attempts to acquire the lock in exclusive mode.
        /// </summary>
        /// <returns>Whether the lock was acquired.</returns>
        public bool TryAcquireExclusive()
        {
            int value;

            value = _value;

            if ((value & LockOwned) != 0)
                return false;

            return Interlocked.CompareExchange(
                ref _value,
                value + LockOwned,
                value
                ) == value;
        }

        /// <summary>
        /// Attempts to acquire the lock in shared mode.
        /// </summary>
        /// <returns>Whether the lock was acquired.</returns>
        public bool TryAcquireShared()
        {
            int value;

            value = _value;

            if (
                (value & LockOwned) == 0 ||
                ((value & LockWaiters) == 0 && ((value >> LockSharedOwnersShift) & LockSharedOwnersMask) != 0)
                )
            {
                if ((value & LockOwned) == 0)
                {
                    if (Interlocked.CompareExchange(
                        ref _value,
                        value + LockOwned + LockSharedOwnersIncrement,
                        value
                        ) == value)
                        return true;
                }
                else
                {
                    if (Interlocked.CompareExchange(
                        ref _value,
                        value + LockSharedOwnersIncrement,
                        value
                        ) == value)
                        return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Unblocks a wait block.
        /// </summary>
        /// <param name="waitBlock">The wait block to unblock.</param>
        private void Unblock(WaitBlock* waitBlock)
        {
            int flags;

            // Clear the spinning flag.
            do
            {
                flags = waitBlock->Flags;
            } while (Interlocked.CompareExchange(
                ref waitBlock->Flags,
                flags & ~WaiterSpinning,
                flags
                ) != flags);

            if ((flags & WaiterSpinning) == 0)
            {
                NativeMethods.NtReleaseKeyedEvent(
                    _wakeEvent,
                    new IntPtr(waitBlock),
                    false,
                    IntPtr.Zero
                    );
            }
        }

        /// <summary>
        /// Wakes either one exclusive waiter or multiple shared waiters.
        /// </summary>
        private void Wake()
        {
            WaitBlock wakeList = new WaitBlock();
            WaitBlock* wb = null;

            wakeList.Flink = &wakeList;
            wakeList.Blink = &wakeList;

            _lock.Acquire();

            try
            {
                bool first = true;

                while (true)
                {
                    wb = _waitersListHead->Flink;

                    if (wb == _waitersListHead)
                    {
                        int value;

                        // No more waiters. Clear the waiters bit.
                        do
                        {
                            value = _value;
                        } while (Interlocked.CompareExchange(
                            ref _value,
                            value & ~LockWaiters,
                            value
                            ) != value);

                        break;
                    }

                    // If this is an exclusive waiter, don't wake 
                    // anyone else.
                    if (first && (wb->Flags & WaiterExclusive) != 0)
                    {
                        wb = RemoveHeadList(_waitersListHead);
                        break;
                    }

                    // If this is not the first waiter we have looked at 
                    // and it is an exclusive waiter, don't wake anyone 
                    // else - it means we have reached the end of a chain 
                    // of shared waiters.
                    if (!first && (wb->Flags & WaiterExclusive) != 0)
                        break;

                    // Remove the waiter and add it to the wake list.
                    wb = RemoveHeadList(_waitersListHead);
                    InsertTailList(&wakeList, wb);

                    first = false;
                }

                if ((wb->Flags & WaiterExclusive) == 0)
                {
                    // If we removed shared waiters, we removed all of them. 
                    // Reset the first shared waiter pointer.
                    _firstSharedWaiter = _waitersListHead;
                }
            }
            finally
            {
                _lock.Release();
            }

            // If we removed one exclusive waiter, unblock it.
            if ((wb->Flags & WaiterExclusive) != 0)
            {
                this.Unblock(wb);
                return;
            }

            // Carefully traverse the wake list and wake each waiter.
            wb = wakeList.Flink;

            while (true)
            {
                WaitBlock* flink;

                if (wb == &wakeList)
                    break;

                flink = wb->Flink;
                this.Unblock(wb);
                wb = flink;
            }
        }

        /// <summary>
        /// Wakes one exclusive waiter.
        /// </summary>
        private void WakeExclusive()
        {
            WaitBlock* wb;
            WaitBlock* unblockWaitBlock = null;

            _lock.Acquire();

            try
            {
                wb = _waitersListHead->Flink;

                if (
                    wb != _waitersListHead &&
                    (wb->Flags & WaiterExclusive) != 0
                    )
                {
                    unblockWaitBlock = RemoveHeadList(_waitersListHead);
                    wb = _waitersListHead->Flink;
                }

                if (wb == _waitersListHead)
                {
                    int value;

                    // No more waiters. Clear the waiters bit.
                    do
                    {
                        value = _value;
                    } while (Interlocked.CompareExchange(
                        ref _value,
                        value & ~LockWaiters,
                        value
                        ) != value);
                }
            }
            finally
            {
                _lock.Release();
            }

            if (unblockWaitBlock != null)
                this.Unblock(unblockWaitBlock);
        }

        /// <summary>
        /// Wakes multiple shared waiters.
        /// </summary>
        private void WakeShared()
        {
            WaitBlock wakeList = new WaitBlock();
            WaitBlock* wb = null;

            wakeList.Flink = &wakeList;
            wakeList.Blink = &wakeList;

            _lock.Acquire();

            try
            {
                wb = _firstSharedWaiter;

                while (true)
                {
                    WaitBlock* flink;

                    if (wb == _waitersListHead)
                    {
                        int value;

                        // No more waiters. Clear the waiters bit.
                        do
                        {
                            value = _value;
                        } while (Interlocked.CompareExchange(
                            ref _value,
                            value & ~LockWaiters,
                            value
                            ) != value);

                        break;
                    }

                    // Remove the waiter and add it to the wake list.
                    flink = wb->Flink;
                    RemoveEntryList(wb);
                    InsertTailList(&wakeList, wb);
                    wb = flink;
                }

                // Reset the first shared waiter pointer.
                _firstSharedWaiter = _waitersListHead;
            }
            finally
            {
                _lock.Release();
            }

            // Carefully traverse the wake list and wake each waiter.
            wb = wakeList.Flink;

            while (true)
            {
                WaitBlock* flink;

                if (wb == &wakeList)
                    break;

                flink = wb->Flink;
                this.Unblock(wb);
                wb = flink;
            }
        }
    }
}
