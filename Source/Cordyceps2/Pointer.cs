namespace Cordyceps2;

// Have to use this wrapper because you can't have pointers as generic type arguments.
public unsafe class Pointer<T>(T* ptr) where T : unmanaged
{
    public T* Value = ptr;

    public static implicit operator T*(Pointer<T> ptr) => ptr.Value;
    public static implicit operator Pointer<T>(T* ptr) => new(ptr);
}