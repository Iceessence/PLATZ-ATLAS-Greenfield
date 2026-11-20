#version 450

layout(location = 0) in vec2 position;
layout(location = 1) in vec3 color;
layout(location = 2) in vec2 imageUv;
layout(location = 3) in vec3 imageData;
layout(location = 0) out vec3 vertexColor;
layout(location = 1) out vec2 uv;
layout(location = 2) flat out ivec3 image;

void main()
{
    gl_Position = vec4(position * 2.0 - 1.0, 0.0, 1.0);
    vertexColor = color;
    uv = imageUv;
    image = ivec3(imageData);
}
